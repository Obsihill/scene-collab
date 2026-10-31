using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;

namespace SceneCollab.Editor
{
    /// <summary>
    /// WebSocket client for one room.
    ///
    /// Threading contract, because getting this wrong is the classic editor-hang bug:
    /// the socket runs on the thread pool, it never touches a Unity API, and everything it
    /// learns is queued and replayed on the main thread from <c>EditorApplication.update</c>.
    /// Events therefore always fire on the main thread, in order.
    /// </summary>
    public sealed class CollabSession : ISceneCollabSession, IDisposable
    {
        enum InboundKind
        {
            SocketOpened,
            Text,
            Closed,
            Error,
            Fault,
        }

        sealed class Inbound
        {
            public InboundKind Kind;
            public string Text;
            public string Detail;
        }

        const int ReceiveBufferBytes = 16 * 1024;

        readonly ConcurrentQueue<Inbound> _inbox = new ConcurrentQueue<Inbound>();
        readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        readonly List<ParticipantInfo> _participants = new List<ParticipantInfo>();

        ClientWebSocket _socket;
        CancellationTokenSource _cancellation;
        bool _pumpAttached;

        public CollabConnectionState State { get; private set; }

        public string ClientId { get; private set; }

        public bool IsScribe { get; private set; }

        public long Revision { get; private set; }

        public string RoomName { get; private set; }

        public IReadOnlyList<ParticipantInfo> Participants
        {
            get { return _participants; }
        }

        public event Action StateChanged;
        public event Action ParticipantsChanged;
        public event Action<WireMessage> OperationReceived;
        public event Action<WireMessage> OperationAcknowledged;
        public event Action<string, string> ErrorReceived;

        public CollabSession()
        {
            State = CollabConnectionState.Disconnected;
            ClientId = "";
            RoomName = "";
        }

        public void Connect(CollabSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if (State == CollabConnectionState.Connecting || State == CollabConnectionState.Connected)
            {
                return;
            }

            RoomName = string.IsNullOrEmpty(settings.room) ? "default" : settings.room;
            SetState(CollabConnectionState.Connecting);
            AttachPump();

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            // Both of these touch editor state (EditorPrefs, the serialized settings asset) and must be
            // read here, on the main thread. Reading them inside RunAsync throws "GetString can only be
            // called from the main thread" and the socket never opens.
            var accessToken = CollabSettings.Token;
            var connectionUrl = settings.BuildConnectionUrl();
            Task.Run(() => RunAsync(accessToken, connectionUrl, token));
        }

        public void Disconnect()
        {
            var cancellation = _cancellation;
            _cancellation = null;
            if (cancellation != null)
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }

            var socket = _socket;
            _socket = null;
            if (socket != null)
            {
                try
                {
                    if (socket.State == WebSocketState.Open)
                    {
                        // Best effort; a server that already vanished must not block the UI.
                        socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None)
                            .Wait(200);
                    }
                }
                catch (Exception)
                {
                    // ignored: closing a dead socket is expected to throw
                }

                try { socket.Dispose(); } catch (Exception) { }
            }

            DetachPump();
            _participants.Clear();
            IsScribe = false;
            ClientId = "";
            SetState(CollabConnectionState.Disconnected);
            RaiseParticipantsChanged();
        }

        public bool SendOperation(SceneOperation operation)
        {
            if (State != CollabConnectionState.Connected)
            {
                return false;
            }

            // Client-generated idempotency key: a reconnect replay of the same op must not apply twice.
            var opId = Guid.NewGuid().ToString("N");
            Send(operation.ToWire(ClientId, RoomName, Revision, opId));
            return true;
        }

        public void Dispose()
        {
            Disconnect();
            _sendGate.Dispose();
        }

        // ── socket side (thread pool) ───────────────────────────────────────────

        async Task RunAsync(string accessToken, string connectionUrl, CancellationToken token)
        {
            var socket = new ClientWebSocket();

            try
            {
                if (!string.IsNullOrEmpty(accessToken))
                {
                    socket.Options.SetRequestHeader("X-SceneCollab-Token", accessToken);
                }

                await socket.ConnectAsync(new Uri(connectionUrl), token).ConfigureAwait(false);

                _socket = socket;
                _inbox.Enqueue(new Inbound { Kind = InboundKind.SocketOpened });
                await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disconnect() caused it.
            }
            catch (Exception ex)
            {
                _inbox.Enqueue(new Inbound { Kind = InboundKind.Fault, Detail = ex.Message });
            }
            finally
            {
                try { socket.Dispose(); } catch (Exception) { }
            }
        }

        async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[ReceiveBufferBytes];

            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using (var stream = new MemoryStream())
                {
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            _inbox.Enqueue(new Inbound { Kind = InboundKind.Closed, Detail = result.CloseStatusDescription });
                            return;
                        }

                        stream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    _inbox.Enqueue(new Inbound
                    {
                        Kind = InboundKind.Text,
                        Text = Encoding.UTF8.GetString(stream.ToArray()),
                    });
                }
            }

            _inbox.Enqueue(new Inbound { Kind = InboundKind.Closed });
        }

        async Task SendTextAsync(WebSocket socket, string json, CancellationToken token)
        {
            var held = false;

            try
            {
                await _sendGate.WaitAsync(token).ConfigureAwait(false);
                held = true;

                if (socket.State != WebSocketState.Open)
                {
                    return;
                }

                await socket
                    .SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                _inbox.Enqueue(new Inbound { Kind = InboundKind.Error, Detail = "send failed: " + ex.Message });
            }
            finally
            {
                if (held)
                {
                    _sendGate.Release();
                }
            }
        }

        void Send(WireMessage message)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
            {
                return;
            }

            var token = _cancellation == null ? CancellationToken.None : _cancellation.Token;
            var json = UnityWireJson.Serialize(message);
            Task.Run(() => SendTextAsync(socket, json, token));
        }

        // ── main thread side ───────────────────────────────────────────────────

        void AttachPump()
        {
            if (_pumpAttached)
            {
                return;
            }

            EditorApplication.update += Pump;
            _pumpAttached = true;
        }

        void DetachPump()
        {
            if (!_pumpAttached)
            {
                return;
            }

            EditorApplication.update -= Pump;
            _pumpAttached = false;
        }

        void Pump()
        {
            Inbound item;
            while (_inbox.TryDequeue(out item))
            {
                switch (item.Kind)
                {
                    case InboundKind.SocketOpened:
                        SendHello();
                        break;

                    case InboundKind.Text:
                        HandleMessage(item.Text);
                        break;

                    case InboundKind.Closed:
                        DetachPump();
                        SetState(CollabConnectionState.Disconnected);
                        break;

                    case InboundKind.Error:
                        FireError(ErrorCodes.Malformed, item.Detail);
                        break;

                    case InboundKind.Fault:
                        DetachPump();
                        SetState(CollabConnectionState.Faulted);
                        FireError(ErrorCodes.NotInitialized, item.Detail);
                        break;
                }
            }
        }

        void SendHello()
        {
            Send(new WireMessage
            {
                type = MessageTypes.Hello,
                protocolVersion = ProtocolVersion.Current,
                displayName = Environment.UserName,
                room = RoomName,
            });
        }

        void HandleMessage(string json)
        {
            WireMessage message;
            try
            {
                message = UnityWireJson.Deserialize(json);
            }
            catch (Exception ex)
            {
                FireError(ErrorCodes.Malformed, "unreadable message: " + ex.Message);
                return;
            }

            if (message == null || string.IsNullOrEmpty(message.type))
            {
                return;
            }

            switch (message.type)
            {
                case MessageTypes.Welcome:
                    ClientId = message.clientId;
                    IsScribe = message.isScribe;
                    Revision = message.revision;
                    if (!string.IsNullOrEmpty(message.room))
                    {
                        RoomName = message.room;
                    }

                    ReplaceParticipants(message.participants);
                    SetState(CollabConnectionState.Connected);
                    break;

                case MessageTypes.Presence:
                    if (message.revision > Revision)
                    {
                        Revision = message.revision;
                    }

                    ReplaceParticipants(message.participants);
                    break;

                case MessageTypes.Op:
                    if (message.revision > Revision)
                    {
                        Revision = message.revision;
                    }

                    var handler = OperationReceived;
                    if (handler != null)
                    {
                        handler(message);
                    }

                    break;

                case MessageTypes.Ack:
                    if (message.revision > Revision)
                    {
                        Revision = message.revision;
                    }

                    var ackHandler = OperationAcknowledged;
                    if (ackHandler != null)
                    {
                        ackHandler(message);
                    }

                    if (!message.accepted)
                    {
                        FireError(
                            string.IsNullOrEmpty(message.code) ? ErrorCodes.StaleRevision : message.code,
                            message.message);
                    }

                    break;

                case MessageTypes.Error:
                    FireError(message.code, message.message);
                    break;
            }
        }

        void ReplaceParticipants(ParticipantInfo[] participants)
        {
            _participants.Clear();
            if (participants != null)
            {
                _participants.AddRange(participants);
            }

            RaiseParticipantsChanged();
        }

        void SetState(CollabConnectionState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            var handler = StateChanged;
            if (handler != null)
            {
                handler();
            }
        }

        void RaiseParticipantsChanged()
        {
            var handler = ParticipantsChanged;
            if (handler != null)
            {
                handler();
            }
        }

        void FireError(string code, string message)
        {
            var handler = ErrorReceived;
            if (handler != null)
            {
                handler(code, message);
            }
        }
    }
}
