using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain.Room;

namespace SceneCollab.Server.WebApi.WebSockets
{
    /// <summary>
    /// One room: the authoritative document plus the sockets currently attached to it.
    /// </summary>
    public sealed class RoomSession
    {
        /// <summary>Guards against a client pushing a huge payload through before it is joined.</summary>
        const int MaxMessageBytes = 4 * 1024 * 1024;

        readonly object _gate = new object();
        readonly Dictionary<string, ClientConnection> _clients =
            new Dictionary<string, ClientConnection>(StringComparer.Ordinal);

        public RoomSession(string key)
        {
            Key = key;
            State = new RoomState();
        }

        public string Key { get; }

        public RoomState State { get; }

        public int ConnectionCount
        {
            get { lock (_gate) { return _clients.Count; } }
        }

        public async Task HandleAsync(WebSocket socket, ClientConnection connection, CancellationToken cancellationToken)
        {
            var joined = false;

            try
            {
                while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                {
                    string json;
                    try
                    {
                        json = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    if (json == null)
                    {
                        break;
                    }

                    WireMessage message;
                    try
                    {
                        message = WireJson.Deserialize(json);
                    }
                    catch (JsonException)
                    {
                        await SendErrorAsync(connection, ErrorCodes.Malformed, "invalid json", cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (message == null || string.IsNullOrEmpty(message.type))
                    {
                        await SendErrorAsync(connection, ErrorCodes.Malformed, "missing type", cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (message.type == MessageTypes.Ping)
                    {
                        await connection
                            .SendAsync(WireJson.Serialize(new WireMessage { type = MessageTypes.Pong, serverTime = Now() }), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (!joined)
                    {
                        if (message.type != MessageTypes.Hello)
                        {
                            await SendErrorAsync(connection, ErrorCodes.NotInitialized, "send hello first", cancellationToken)
                                .ConfigureAwait(false);
                            continue;
                        }

                        joined = await HandleHelloAsync(connection, message, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    switch (message.type)
                    {
                        case MessageTypes.Hello:
                            await SendErrorAsync(connection, ErrorCodes.Malformed, "already joined", cancellationToken)
                                .ConfigureAwait(false);
                            break;

                        case MessageTypes.Op:
                            await HandleOpAsync(connection, message, cancellationToken).ConfigureAwait(false);
                            break;

                        case MessageTypes.Presence:
                            if (State.UpdatePresence(connection.ClientId, message.selectedNodeIds))
                            {
                                await BroadcastPresenceAsync(null, cancellationToken).ConfigureAwait(false);
                            }

                            break;

                        default:
                            await SendErrorAsync(connection, ErrorCodes.Malformed, "unknown type " + message.type, cancellationToken)
                                .ConfigureAwait(false);
                            break;
                    }
                }
            }
            finally
            {
                await RemoveAsync(connection).ConfigureAwait(false);
            }
        }

        async Task<bool> HandleHelloAsync(ClientConnection connection, WireMessage message, CancellationToken cancellationToken)
        {
            if (!ProtocolVersion.IsCompatible(message.protocolVersion))
            {
                await SendErrorAsync(
                        connection,
                        ErrorCodes.ProtocolMismatch,
                        "server supports protocol " + ProtocolVersion.MinCompatible + ".." + ProtocolVersion.Current
                            + ", client sent " + message.protocolVersion,
                        cancellationToken)
                    .ConfigureAwait(false);

                await connection
                    .CloseAsync(WebSocketCloseStatus.PolicyViolation, ErrorCodes.ProtocolMismatch, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

            connection.DisplayName = SanitizeName(message.displayName, connection.ClientId);

            var participant = State.Join(connection.ClientId, connection.DisplayName, message.color);
            connection.Color = participant.color;

            lock (_gate)
            {
                _clients[connection.ClientId] = connection;
            }

            var welcome = new WireMessage
            {
                type = MessageTypes.Welcome,
                protocolVersion = ProtocolVersion.Current,
                clientId = connection.ClientId,
                displayName = participant.displayName,
                color = participant.color,
                room = Key,
                revision = State.Revision,
                isScribe = participant.isScribe,
                participants = State.SnapshotParticipants(),
                serverTime = Now(),
            };

            await connection.SendAsync(WireJson.Serialize(welcome), cancellationToken).ConfigureAwait(false);

            // The joiner already learned the roster from its welcome; sending it a presence
            // snapshot too would just arrive out of order behind the welcome.
            await BroadcastPresenceAsync(connection.ClientId, cancellationToken).ConfigureAwait(false);
            return true;
        }

        async Task HandleOpAsync(ClientConnection connection, WireMessage message, CancellationToken cancellationToken)
        {
            // Never trust the identity a client claims for itself.
            message.clientId = connection.ClientId;

            var result = State.Apply(message);

            var ack = new WireMessage
            {
                type = MessageTypes.Ack,
                opId = message.opId,
                accepted = result.Accepted,
                duplicate = result.Duplicate,
                revision = result.Revision,
                code = result.Accepted ? null : result.Code,
                message = result.Accepted ? null : result.Message,
                clientId = connection.ClientId,
                room = Key,
                serverTime = Now(),
            };

            await connection.SendAsync(WireJson.Serialize(ack), cancellationToken).ConfigureAwait(false);

            if (!result.Accepted || result.Duplicate)
            {
                return;
            }

            var record = result.Record;
            var broadcast = new WireMessage
            {
                type = MessageTypes.Op,
                opId = record.OpId,
                clientId = record.ClientId,
                // Carried here so a receiver can label the edit ("Obsihill moved Cube") without
                // having to match clientId against a roster it may have missed.
                displayName = connection.DisplayName,
                opKind = record.Kind,
                nodeId = record.NodeId,
                propertyPath = record.PropertyPath,
                oldValue = record.OldValue,
                newValue = record.NewValue,
                revision = record.Revision,
                room = Key,
                serverTime = record.TimestampUnixMs,
            };

            await BroadcastAsync(WireJson.Serialize(broadcast), connection.ClientId, cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task BroadcastPresenceAsync(string exceptClientId, CancellationToken cancellationToken)
        {
            var presence = new WireMessage
            {
                type = MessageTypes.Presence,
                room = Key,
                revision = State.Revision,
                participants = State.SnapshotParticipants(),
                serverTime = Now(),
            };

            await BroadcastAsync(WireJson.Serialize(presence), exceptClientId, cancellationToken).ConfigureAwait(false);
        }

        async Task BroadcastAsync(string json, string exceptClientId, CancellationToken cancellationToken)
        {
            ClientConnection[] snapshot;
            lock (_gate)
            {
                snapshot = new List<ClientConnection>(_clients.Values).ToArray();
            }

            foreach (var client in snapshot)
            {
                if (exceptClientId != null && string.Equals(client.ClientId, exceptClientId, StringComparison.Ordinal))
                {
                    continue;
                }

                await client.SendAsync(json, cancellationToken).ConfigureAwait(false);
            }
        }

        async Task RemoveAsync(ClientConnection connection)
        {
            bool removed;
            lock (_gate)
            {
                removed = _clients.Remove(connection.ClientId);
            }

            if (!removed)
            {
                return;
            }

            State.Leave(connection.ClientId);

            // The request token is already cancelled here in most disconnects.
            await BroadcastPresenceAsync(null, CancellationToken.None).ConfigureAwait(false);
        }

        async Task SendErrorAsync(ClientConnection connection, string code, string message, CancellationToken cancellationToken)
        {
            var error = new WireMessage
            {
                type = MessageTypes.Error,
                code = code,
                message = message,
                clientId = connection.ClientId,
                room = Key,
                revision = State.Revision,
                serverTime = Now(),
            };

            await connection.SendAsync(WireJson.Serialize(error), cancellationToken).ConfigureAwait(false);
        }

        static async Task<string> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            using (var stream = new MemoryStream())
            {
                while (true)
                {
                    var result = await socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }

                    stream.Write(buffer, 0, result.Count);

                    if (stream.Length > MaxMessageBytes)
                    {
                        throw new InvalidOperationException("message exceeds " + MaxMessageBytes + " bytes");
                    }

                    if (result.EndOfMessage)
                    {
                        break;
                    }
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        static string SanitizeName(string requested, string fallback)
        {
            if (string.IsNullOrWhiteSpace(requested))
            {
                return fallback;
            }

            var builder = new StringBuilder(requested.Length);
            foreach (var c in requested.Trim())
            {
                if (!char.IsControl(c) && builder.Length < 64)
                {
                    builder.Append(c);
                }
            }

            return builder.Length == 0 ? fallback : builder.ToString();
        }

        static long Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }
}
