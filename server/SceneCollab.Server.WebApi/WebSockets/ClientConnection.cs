using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SceneCollab.Server.WebApi.WebSockets
{
    /// <summary>
    /// A single connected editor.
    ///
    /// Sends are serialised with a semaphore because broadcasts are issued from *other*
    /// clients' receive loops: two concurrent SendAsync calls on one WebSocket throw.
    /// </summary>
    public sealed class ClientConnection
    {
        readonly WebSocket _socket;
        readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
        static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

        public ClientConnection(WebSocket socket, string clientId, string roomKey)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            ClientId = clientId;
            RoomKey = roomKey;
            DisplayName = clientId;
            Color = "#888888";
            LastSeenUtc = DateTime.UtcNow;
        }

        public string ClientId { get; }

        public string RoomKey { get; }

        public string DisplayName { get; set; }

        public string Color { get; set; }

        /// <summary>
        /// When this peer was last heard from, by any frame at all. Written by the receive loop, read by
        /// the heartbeat: a socket can look perfectly open after the editor behind it has gone away.
        /// </summary>
        public DateTime LastSeenUtc { get; set; }

        public bool IsOpen
        {
            get { return _socket.State == WebSocketState.Open; }
        }

        public async Task SendAsync(string json, CancellationToken cancellationToken)
        {
            if (!IsOpen)
            {
                return;
            }

            var payload = Encoding.UTF8.GetBytes(json);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(SendTimeout);
            var entered = false;
            try
            {
                await _sendGate.WaitAsync(timeout.Token).ConfigureAwait(false);
                entered = true;
                if (!IsOpen)
                {
                    return;
                }

                await _socket
                    .SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, timeout.Token)
                    .WaitAsync(timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A broken/blocked peer cannot hold the room's ordered delivery gate forever.
                // Abort also wakes its receive loop so membership and leases get cleaned up.
                try { _socket.Abort(); } catch (Exception) { }
            }
            finally
            {
                if (entered) _sendGate.Release();
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken cancellationToken)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(SendTimeout);
                if (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived)
                {
                    await _socket
                        .CloseAsync(status, description ?? string.Empty, timeout.Token)
                        .WaitAsync(timeout.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                try { _socket.Abort(); } catch (Exception) { }
            }
        }
    }
}
