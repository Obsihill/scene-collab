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
            Reconnecting,
            Silent,
        }

        sealed class Inbound
        {
            public InboundKind Kind;
            public string Text;
            public string Detail;
            public WireMessage Message;
        }

        const int ReceiveBufferBytes = 16 * 1024;

        /// <summary>Same cadence the server uses, so neither side mistakes the other for dead.</summary>
        static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);

        /// <summary>
        /// A minute of silence ends the connection. The server says the same about us: a socket can stay
        /// "open" for hours behind a proxy after the machine at the other end has gone.
        /// </summary>
        static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(60);

        /// <summary>Reconnect delays, in seconds. The last entry repeats.</summary>
        static readonly int[] ReconnectDelaysSeconds = { 1, 2, 4, 8, 15, 30 };

        readonly ConcurrentQueue<Inbound> _inbox = new ConcurrentQueue<Inbound>();
        readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        readonly List<ParticipantInfo> _participants = new List<ParticipantInfo>();

        ClientWebSocket _socket;
        CancellationTokenSource _cancellation;
        bool _pumpAttached;
        string _displayName;

        // Ticks, not DateTime: written by the socket thread and read by the main thread, and a torn
        // DateTime read is not something anybody wants to debug.
        long _lastInboundTicks;
        long _lastPingSentTicks;
        volatile bool _keepAliveWanted;

        public CollabConnectionState State { get; private set; }

        public string ClientId { get; private set; }

        public bool IsScribe { get; private set; }

        /// <summary>True when this editor opened the scene and is therefore the one that saves.</summary>
        public bool IsHost { get; private set; }

        public string SceneGuid { get; private set; }

        public string ScenePath { get; private set; }

        public string SceneName { get; private set; }

        /// <summary>Node ids currently locked by someone in the room, as last announced by the server.</summary>
        public string[] LockedNodeIds { get; private set; }

        /// <summary>Number of assets the host's scene depends on (0 until the host publishes).</summary>
        public int AssetCount { get; private set; }

        /// <summary>Asset guids the host's scene depends on, as last published ("" until then).</summary>
        public string[] AssetGuids { get; private set; }

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

        /// <summary>A peer's scene-view camera pose. Drawn as an overlay; never applied to the scene.</summary>
        public event Action<WireMessage> ViewReceived;

        /// <summary>A terrain patch from the host (v0.5). Applies to a TerrainData asset, not a node.</summary>
        public event Action<WireMessage> TerrainReceived;
        public event Action<string, string> ErrorReceived;
        public event Action LocksChanged;
        public event Action<int> AssetCountChanged;
        public event Action AssetManifestChanged;

        public CollabSession()
        {
            State = CollabConnectionState.Disconnected;
            ClientId = "";
            RoomName = "";
            SceneGuid = "";
            ScenePath = "";
            SceneName = "";
            LockedNodeIds = new string[0];
            AssetGuids = new string[0];
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
            _keepAliveWanted = true;
            TouchInbound();
            SetState(CollabConnectionState.Connecting);
            AttachPump();

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            // Both of these touch editor state (EditorPrefs, the serialized settings asset) and must be
            // read here, on the main thread. Reading them inside RunAsync throws "GetString can only be
            // called from the main thread" and the socket never opens.
            var accessToken = CollabSettings.Token;
            var connectionUrl = settings.BuildConnectionUrl();
            var identity = SceneSync.SceneIdentity.CaptureActive();
            var displayName = string.IsNullOrEmpty(settings.displayName)
                ? Environment.UserName
                : settings.displayName.Trim();
            _displayName = displayName;

            Task.Run(() => RunAsync(accessToken, connectionUrl, displayName, identity, token));
        }

        public void Disconnect()
        {
            // Cleared first: the run loop reads this to decide whether a dropped socket is worth
            // reconnecting, and Disconnect() is the one case where it is not.
            _keepAliveWanted = false;

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
            IsHost = false;
            ClientId = "";
            SceneGuid = "";
            ScenePath = "";
            SceneName = "";
            LockedNodeIds = new string[0];
            AssetGuids = new string[0];
            SetState(CollabConnectionState.Disconnected);
            RaiseParticipantsChanged();
            RaiseLocksChanged();
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

        /// <summary>
        /// Sends one snapshot frame. Frames are ops on the wire so they stay ordered with edits, and
        /// like any op they carry the revision this editor had applied — the caller must therefore
        /// pace them one ack at a time, exactly as it paces edits.
        /// </summary>
        public bool SendSnapshotFrame(WireMessage frame)
        {
            if (State != CollabConnectionState.Connected || frame == null)
            {
                return false;
            }

            frame.type = MessageTypes.Op;
            frame.protocolVersion = ProtocolVersion.Current;
            frame.clientId = ClientId;
            frame.room = RoomName;
            frame.revision = Revision;
            frame.opId = Guid.NewGuid().ToString("N");
            frame.displayName = _displayName;
            frame.snapshot = true;
            Send(frame);
            return true;
        }

        /// <summary>
        /// Broadcasts this editor's scene-view camera pose. Fire-and-forget by design: a dropped pose
        /// is superseded by the next one in a tenth of a second.
        /// </summary>
        public bool SendView(
            float posX,
            float posY,
            float posZ,
            float rotX,
            float rotY,
            float rotZ,
            float rotW,
            float fov)
        {
            if (State != CollabConnectionState.Connected)
            {
                return false;
            }

            Send(new WireMessage
            {
                type = MessageTypes.View,
                clientId = ClientId,
                room = RoomName,
                displayName = _displayName,
                sceneGuid = SceneGuid,
                viewValid = true,
                viewPosX = posX,
                viewPosY = posY,
                viewPosZ = posZ,
                viewRotX = rotX,
                viewRotY = rotY,
                viewRotZ = rotZ,
                viewRotW = rotW,
                viewFov = fov,
            });
            return true;
        }

        /// <summary>
        /// Sends one rectangle of terrain samples (v0.5). Fire-and-forget: a tile is small, always
        /// superseded by the next stroke tile, and never part of the op log.
        /// </summary>
        public bool SendTerrain(WireMessage patch)
        {
            if (State != CollabConnectionState.Connected || patch == null)
            {
                return false;
            }

            patch.type = MessageTypes.Terrain;
            patch.clientId = ClientId;
            patch.room = RoomName;
            patch.displayName = _displayName;
            patch.sceneGuid = SceneGuid;
            patch.terrainValid = true;
            Send(patch);
            return true;
        }

        /// <summary>
        /// Ask the server for (or release) the edit lock on a node. The server answers everyone with the
        /// full lock set, so the local view is only ever replaced, never patched.
        /// </summary>
        public bool SendLock(string nodeId, bool release)
        {
            if (State != CollabConnectionState.Connected || string.IsNullOrEmpty(nodeId))
            {
                return false;
            }

            Send(new WireMessage
            {
                type = release ? MessageTypes.Unlock : MessageTypes.Lock,
                clientId = ClientId,
                room = RoomName,
                lockNodeId = nodeId,
                displayName = _displayName,
            });

            return true;
        }

        /// <summary>
        /// Publish the asset guids the host's scene depends on (host only). Every joiner resolves the
        /// list against its own AssetDatabase and stands placeholders in for whatever it lacks.
        /// </summary>
        public bool PublishAssets(string[] assetGuids)
        {
            if (State != CollabConnectionState.Connected || !IsHost)
            {
                return false;
            }

            Send(new WireMessage
            {
                type = MessageTypes.Assets,
                clientId = ClientId,
                room = RoomName,
                assetGuids = assetGuids ?? new string[0],
                assetCount = assetGuids == null ? 0 : assetGuids.Length,
            });

            return true;
        }

        /// <summary>Count-only overload kept for callers that have not moved to a manifest yet.</summary>
        public bool PublishAssetCount(int count)
        {
            return PublishAssets(new string[Math.Max(0, count)]);
        }

        public bool IsLockedByOther(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || LockedNodeIds == null)
            {
                return false;
            }

            return Array.IndexOf(LockedNodeIds, nodeId) >= 0;
        }

        public void Dispose()
        {
            Disconnect();
            _sendGate.Dispose();
        }

        // ── socket side (thread pool) ───────────────────────────────────────────

        async Task RunAsync(
            string accessToken,
            string connectionUrl,
            string displayName,
            SceneSync.SceneIdentity identity,
            CancellationToken token)
        {
            var attempt = 0;

            while (!token.IsCancellationRequested)
            {
                var wasConnected = await RunOnceAsync(accessToken, connectionUrl, displayName, identity, token)
                    .ConfigureAwait(false);

                if (token.IsCancellationRequested || !_keepAliveWanted)
                {
                    return;
                }

                // A connection that worked earns a fresh backoff: only a run of failures climbs the ladder.
                attempt = wasConnected ? 1 : attempt + 1;
                var delay = ReconnectDelaysSeconds[Math.Min(attempt - 1, ReconnectDelaysSeconds.Length - 1)];

                UnityEngine.Debug.Log("[scene-collab] connection lost; retrying in " + delay + " s");

                _inbox.Enqueue(new Inbound
                {
                    Kind = InboundKind.Reconnecting,
                    Detail = delay.ToString(),
                });

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>One connection attempt. Returns whether it ever got as far as opening the socket.</summary>
        async Task<bool> RunOnceAsync(
            string accessToken,
            string connectionUrl,
            string displayName,
            SceneSync.SceneIdentity identity,
            CancellationToken token)
        {
            var socket = new ClientWebSocket();
            var opened = false;

            try
            {
                if (!string.IsNullOrEmpty(accessToken))
                {
                    socket.Options.SetRequestHeader("X-SceneCollab-Token", accessToken);
                }

                await socket.ConnectAsync(new Uri(connectionUrl), token).ConfigureAwait(false);
                opened = true;
                TouchInbound();

                _socket = socket;
                _inbox.Enqueue(new Inbound
                {
                    Kind = InboundKind.SocketOpened,
                    Message = new WireMessage
                    {
                        type = MessageTypes.Hello,
                        protocolVersion = ProtocolVersion.Current,
                        displayName = displayName,
                        room = RoomName,
                        sceneGuid = identity.guid,
                        scenePath = identity.path,
                        sceneName = identity.name,
                    },
                });
                await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disconnect() caused it.
            }
            catch (Exception ex)
            {
                // Dropping the socket on purpose (silence, sleep/wake) is not a fault to show the user:
                // the run loop is about to reconnect.
                if (_keepAliveWanted)
                {
                    _inbox.Enqueue(new Inbound { Kind = InboundKind.Closed, Detail = ex.Message });
                }
                else
                {
                    _inbox.Enqueue(new Inbound { Kind = InboundKind.Fault, Detail = ex.Message });
                }
            }
            finally
            {
                try { socket.Dispose(); } catch (Exception) { }
            }

            return opened;
        }

        /// <summary>Serialised once: every server ping is answered with this exact frame.</summary>
        static readonly string PongFrameJson = UnityWireJson.Serialize(new WireMessage { type = MessageTypes.Pong });

        /// <summary>
        /// True for the server's liveness probe. The cheap substring guard keeps every ordinary
        /// message off the deserialiser; only text that mentions "ping" is parsed to be sure.
        /// </summary>
        static bool IsPingFrame(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("\"ping\"", StringComparison.Ordinal) < 0)
            {
                return false;
            }

            try
            {
                var message = UnityWireJson.Deserialize(text);
                return message != null && message.type == MessageTypes.Ping;
            }
            catch (Exception)
            {
                return false;
            }
        }

        async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
        {
            var buffer = new byte[ReceiveBufferBytes];

            using (var stream = new MemoryStream())
            while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                stream.SetLength(0);
                WebSocketReceiveResult result;
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _inbox.Enqueue(new Inbound { Kind = InboundKind.Closed, Detail = result.CloseStatusDescription });
                    return;
                }

                string text;
                if (result.EndOfMessage)
                {
                    // Most frames fit in the reusable receive buffer; avoid a stream and byte copy.
                    text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                }
                else
                {
                    stream.Write(buffer, 0, result.Count);
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
                    text = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
                    // Do not retain an unusually large frame for the lifetime of the connection.
                    if (stream.Capacity > 1024 * 1024) { stream.SetLength(0); stream.Capacity = 0; }
                }

                TouchInbound();
                if (IsPingFrame(text))
                {
                    await SendTextAsync(socket, PongFrameJson, token).ConfigureAwait(false);
                    continue;
                }
                _inbox.Enqueue(new Inbound { Kind = InboundKind.Text, Text = text });
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
            // Enter the send gate in call order. Scheduling each message on the thread pool can
            // reorder overlapping terrain snapshots and live edits before they reach the socket.
            _ = SendTextAsync(socket, json, token);
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
                        Send(item.Message);
                        break;

                    case InboundKind.Text:
                        HandleMessage(item.Text);
                        break;

                    case InboundKind.Closed:
                        if (_keepAliveWanted)
                        {
                            // The run loop is already backing off; the pump stays attached so the
                            // reconnect's welcome is handled without the user clicking anything.
                            SetState(CollabConnectionState.Connecting);
                            break;
                        }

                        DetachPump();
                        SetState(CollabConnectionState.Disconnected);
                        break;

                    case InboundKind.Reconnecting:
                        SetState(CollabConnectionState.Connecting);
                        break;

                    case InboundKind.Silent:
                        // Aborting is what makes the parked receive call return; the run loop then
                        // reconnects. Closing politely here would wait on a peer that never answers.
                        var dead = _socket;
                        if (dead != null)
                        {
                            try { dead.Abort(); } catch (Exception) { }
                        }

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

            TickKeepAlive();
        }

        /// <summary>Records that something arrived from the server, from either thread.</summary>
        void TouchInbound()
        {
            Interlocked.Exchange(ref _lastInboundTicks, DateTime.UtcNow.Ticks);
        }

        /// <summary>
        /// Main-thread heartbeat. Answers the question the socket API cannot: is the other end still
        /// there? Silence past <see cref="SilenceTimeout"/> kills the socket so the run loop reconnects.
        /// </summary>
        void TickKeepAlive()
        {
            if (!_keepAliveWanted || State != CollabConnectionState.Connected || _socket == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            var lastInbound = new DateTime(Interlocked.Read(ref _lastInboundTicks), DateTimeKind.Utc);
            if (now - lastInbound > SilenceTimeout)
            {
                _inbox.Enqueue(new Inbound { Kind = InboundKind.Silent });
                return;
            }

            var lastPing = new DateTime(Interlocked.Read(ref _lastPingSentTicks), DateTimeKind.Utc);
            if (now - lastPing >= HeartbeatInterval)
            {
                Interlocked.Exchange(ref _lastPingSentTicks, now.Ticks);
                Send(new WireMessage { type = MessageTypes.Ping });
            }
        }

        void HandleMessage(string json)
        {
            TouchInbound();

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
                    IsHost = message.isHost;
                    Revision = message.revision;
                    SceneGuid = message.sceneGuid ?? "";
                    ScenePath = message.scenePath ?? "";
                    SceneName = message.sceneName ?? "";
                    LockedNodeIds = message.lockedNodeIds ?? new string[0];
                    AssetCount = message.assetCount;
                    AssetGuids = message.assetGuids ?? new string[0];
                    if (!string.IsNullOrEmpty(message.room))
                    {
                        RoomName = message.room;
                    }

                    ReplaceParticipants(message.participants);
                    SetState(CollabConnectionState.Connected);
                    RaiseLocksChanged();
                    RaiseAssetManifestChanged();
                    break;

                case MessageTypes.Lock:
                case MessageTypes.Unlock:
                    LockedNodeIds = message.lockedNodeIds ?? new string[0];
                    RaiseLocksChanged();
                    break;

                case MessageTypes.Assets:
                    AssetGuids = message.assetGuids ?? new string[0];

                    AssetCount = message.assetCount > 0 ? message.assetCount : AssetGuids.Length;
                    var assetHandler = AssetCountChanged;
                    if (assetHandler != null)
                    {
                        assetHandler(AssetCount);
                    }

                    RaiseAssetManifestChanged();
                    break;

                case MessageTypes.Presence:
                    if (message.revision > Revision)
                    {
                        Revision = message.revision;
                    }

                    ReplaceParticipants(message.participants);
                    break;

                case MessageTypes.Ping:
                    // The server's liveness probe. Answering is what keeps this editor in the room.
                    Send(new WireMessage { type = MessageTypes.Pong });
                    break;

                case MessageTypes.View:
                    // Camera poses are a presence detail, not an edit: they never touch the revision and
                    // are never applied to the scene, only drawn.
                    var viewHandler = ViewReceived;
                    if (viewHandler != null)
                    {
                        viewHandler(message);
                    }

                    break;

                case MessageTypes.Terrain:
                    // Terrain lives in an asset, not in the scene: patches never touch the revision and
                    // ride their own channel. Applying them is TerrainSync's job.
                    var terrainHandler = TerrainReceived;
                    if (terrainHandler != null)
                    {
                        terrainHandler(message);
                    }

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

        void RaiseAssetManifestChanged()
        {
            var handler = AssetManifestChanged;
            if (handler != null)
            {
                handler();
            }
        }

        void RaiseLocksChanged()
        {
            var handler = LocksChanged;
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
