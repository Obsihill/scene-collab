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
    public sealed partial class RoomSession
    {
        /// <summary>Guards against a client pushing a huge payload through before it is joined.</summary>
        const int MaxMessageBytes = 4 * 1024 * 1024;

        readonly object _gate = new object();
        readonly SemaphoreSlim _operationGate = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, ClientConnection> _clients =
            new Dictionary<string, ClientConnection>(StringComparer.Ordinal);
        readonly Func<long> _utcNow;

        int _assetCount;

        // The host's asset guid list, kept so late joiners learn it in their welcome instead of
        // waiting for the next manifest push.
        string[] _assetGuids = new string[0];

        const int MaxAssetGuids = 8192;

        public RoomSession(string key, Func<long> utcNow = null)
        {
            Key = key;
            State = new RoomState();
            _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public string Key { get; }

        public RoomState State { get; }

        public int ConnectionCount
        {
            get { lock (_gate) { return _clients.Count; } }
        }

        public int LockCount
        {
            get { return State.SnapshotLocks().Length; }
        }

        public string SceneName
        {
            get { return State.SceneName; }
        }

        public string SceneGuid
        {
            get { return State.SceneGuid; }
        }

        public string HostDisplayName
        {
            get
            {
                var hostId = State.HostClientId;
                if (string.IsNullOrEmpty(hostId))
                {
                    return null;
                }

                var roster = State.SnapshotParticipants();
                for (var i = 0; i < roster.Length; i++)
                {
                    if (string.Equals(roster[i].clientId, hostId, StringComparison.Ordinal))
                    {
                        return roster[i].displayName;
                    }
                }

                return null;
            }
        }

        public async Task HandleAsync(WebSocket socket, ClientConnection connection, CancellationToken cancellationToken)
        {
            var joined = false;
            var heartbeat = new CancellationTokenSource();

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

                    connection.LastSeenUtc = DateTime.UtcNow;

                    WireMessage message;
                    try
                    {
                        message = WireJson.Deserialize(json);
                    }
                    catch (JsonException)
                    {
                        await InOrderAsync(() => SendErrorAsync(connection, ErrorCodes.Malformed, "invalid json", CancellationToken.None), cancellationToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    if (message == null || string.IsNullOrEmpty(message.type))
                    {
                        await InOrderAsync(() => SendErrorAsync(connection, ErrorCodes.Malformed, "missing type", CancellationToken.None, message), cancellationToken)
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

                    if (message.type == MessageTypes.Pong)
                    {
                        // Liveness only. LastSeenUtc was already refreshed when the frame was read.
                        continue;
                    }

                    if (!joined)
                    {
                        if (message.type != MessageTypes.Hello)
                        {
                            await InOrderAsync(() => SendErrorAsync(connection, ErrorCodes.NotInitialized, "send hello first", CancellationToken.None), cancellationToken)
                                .ConfigureAwait(false);
                            continue;
                        }

                        joined = await HandleHelloAsync(connection, message, cancellationToken).ConfigureAwait(false);
                        if (joined)
                        {
                            StartHeartbeat(socket, connection, heartbeat.Token);
                        }

                        continue;
                    }

                    await InOrderAsync(() => HandleJoinedMessageCoreAsync(connection, message), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A cancelled request still leaves through the ordered lifecycle path below.
            }
            finally
            {
                heartbeat.Cancel();
                heartbeat.Dispose();
                await RemoveAsync(connection).ConfigureAwait(false);
            }
        }

        Task<bool> HandleHelloAsync(ClientConnection connection, WireMessage message, CancellationToken cancellationToken)
        {
            return InOrderAsync(() => HandleHelloCoreAsync(connection, message, CancellationToken.None), cancellationToken);
        }

        async Task<bool> HandleHelloCoreAsync(ClientConnection connection, WireMessage message, CancellationToken cancellationToken)
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

            // The room is bound to the scene the host opened. A client with a different scene open is
            // refused here instead of joining and having every op silently miss its node.
            string conflict;
            if (!State.TryBindScene(message.sceneGuid, message.scenePath, message.sceneName, out conflict))
            {
                await SendErrorAsync(connection, ErrorCodes.SceneMismatch, conflict, cancellationToken)
                    .ConfigureAwait(false);
                await connection
                    .CloseAsync(WebSocketCloseStatus.PolicyViolation, ErrorCodes.SceneMismatch, cancellationToken)
                    .ConfigureAwait(false);
                return false;
            }

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
                isHost = participant.isHost,
                sceneGuid = State.SceneGuid,
                scenePath = State.ScenePath,
                sceneName = State.SceneName,
                lockedNodeIds = State.SnapshotLocks(),
                terrainLocks = State.TerrainLeases.Snapshot(),
                assetCount = _assetCount,
                assetGuids = _assetGuids,
                participants = State.SnapshotParticipants(),
                serverTime = Now(),
            };

            await connection.SendAsync(WireJson.Serialize(welcome), cancellationToken).ConfigureAwait(false);

            // Welcome and the complete roster must arrive before the new recovery epoch.
            await BroadcastPresenceCoreAsync(null, cancellationToken).ConfigureAwait(false);
            await BeginSyncCoreAsync().ConfigureAwait(false);
            return true;
        }

        Task HandleOpAsync(ClientConnection connection, WireMessage message, CancellationToken cancellationToken)
        {
            return InOrderAsync(() => ApplyAndBroadcastOpAsync(connection, message), cancellationToken);
        }

        async Task ApplyAndBroadcastOpAsync(ClientConnection connection, WireMessage message)
        {
            // Once applied, delivery must not be cancelled by the sender disconnecting.
            var cancellationToken = CancellationToken.None;
            // Never trust the identity a client claims for itself.
            message.clientId = connection.ClientId;

            var rejection = ValidateSnapshotSource(connection, message, message.snapshot || OpKinds.IsSnapshot(message.opKind));
            var result = rejection == null ? State.Apply(message)
                : ApplyResult.Reject(rejection, State.Revision, SyncRejectionMessage(rejection));
            if (result.Accepted && !result.Duplicate && message.opKind == OpKinds.SnapshotEnd)
                _snapshotEndSeen = true;

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
                syncId = message.syncId,
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
                // A broadcast that reaches a peer has already been accepted by the room; the old `false`
                // read as "rejected" in client-side logs.
                accepted = true,
                opKind = record.Kind,
                nodeId = record.NodeId,
                propertyPath = record.PropertyPath,
                oldValue = record.OldValue,
                newValue = record.NewValue,
                parentNodeId = message.parentNodeId,
                siblingIndex = message.siblingIndex,
                componentType = message.componentType,
                componentIndex = message.componentIndex,
                prefabGuid = message.prefabGuid,
                // Marks the op as part of a host snapshot so the receiver applies it without treating it
                // as a concurrent edit (no conflict check, no undo entry).
                snapshot = message.snapshot,
                syncId = message.syncId,
                revision = record.Revision,
                room = Key,
                serverTime = record.TimestampUnixMs,
            };

            await BroadcastAsync(WireJson.Serialize(broadcast), connection.ClientId, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Relays a peer's scene-view camera pose. Deliberately outside the op log: a camera moved 10x a
        /// second is presence, not an edit, and letting it increment the revision would make every room
        /// look busy and every op stale.
        /// </summary>
        async Task HandleViewAsync(ClientConnection connection, WireMessage message, CancellationToken cancellationToken)
        {
            var broadcast = new WireMessage
            {
                type = MessageTypes.View,
                clientId = connection.ClientId,
                displayName = connection.DisplayName,
                room = Key,
                revision = State.Revision,
                viewValid = true,
                viewPosX = message.viewPosX,
                viewPosY = message.viewPosY,
                viewPosZ = message.viewPosZ,
                viewRotX = message.viewRotX,
                viewRotY = message.viewRotY,
                viewRotZ = message.viewRotZ,
                viewRotW = message.viewRotW,
                viewFov = message.viewFov,
                serverTime = Now(),
            };

            await BroadcastAsync(WireJson.Serialize(broadcast), connection.ClientId, cancellationToken)
                .ConfigureAwait(false);
        }

        async Task HandleAssetsAsync(ClientConnection connection, WireMessage message, CancellationToken cancellationToken)
        {
            if (!string.Equals(State.HostClientId, connection.ClientId, StringComparison.Ordinal))
            {
                await SendErrorAsync(connection, ErrorCodes.NotHost, "only the host publishes the asset manifest",
                        cancellationToken, message)
                    .ConfigureAwait(false);
                return;
            }

            _assetGuids = message.assetGuids ?? new string[0];
            if (_assetGuids.Length > MaxAssetGuids)
            {
                _assetGuids = _assetGuids[..MaxAssetGuids];
            }

            _assetCount = _assetGuids.Length > 0
                ? _assetGuids.Length
                : message.assetCount;

            // The guid list crosses the wire, not just the count: a client can only decide "I am missing
            // this mesh" by resolving each guid against its own AssetDatabase.
            var notice = new WireMessage
            {
                type = MessageTypes.Assets,
                room = Key,
                assetGuids = _assetGuids,
                assetCount = _assetCount,
                isHost = true,
                revision = State.Revision,
                serverTime = Now(),
            };

            await BroadcastAsync(WireJson.Serialize(notice), connection.ClientId, cancellationToken)
                .ConfigureAwait(false);
        }

        async Task HandleLockAsync(ClientConnection connection, WireMessage message, bool release, CancellationToken cancellationToken)
        {
            var nodeId = message.lockNodeId;

            if (_syncActive && !release)
            {
                await SendErrorAsync(connection, ErrorCodes.SyncInProgress, "room recovery is in progress", cancellationToken, message)
                    .ConfigureAwait(false);
                return;
            }

            if (release)
            {
                State.Unlock(connection.ClientId, nodeId);
            }
            else
            {
                string owner;
                if (!State.TryLock(connection.ClientId, nodeId, out owner))
                {
                    await SendErrorAsync(connection, ErrorCodes.Locked, "node is locked by " + owner, cancellationToken, message)
                        .ConfigureAwait(false);
                    return;
                }
            }

            // Everyone, including the requester, gets the full lock set: incremental lock deltas are a
            // reliable way to end up with divergent lock state after one missed frame.
            var broadcast = new WireMessage
            {
                type = release ? MessageTypes.Unlock : MessageTypes.Lock,
                room = Key,
                lockNodeId = nodeId,
                clientId = connection.ClientId,
                displayName = connection.DisplayName,
                lockedNodeIds = State.SnapshotLocks(),
                revision = State.Revision,
                serverTime = Now(),
            };

            await BroadcastAsync(WireJson.Serialize(broadcast), null, cancellationToken).ConfigureAwait(false);
        }

        public Task BroadcastPresenceAsync(string exceptClientId, CancellationToken cancellationToken)
        {
            return InOrderAsync(() => BroadcastPresenceCoreAsync(exceptClientId, CancellationToken.None), cancellationToken);
        }

        async Task BroadcastPresenceCoreAsync(string exceptClientId, CancellationToken cancellationToken)
        {
            var presence = new WireMessage
            {
                type = MessageTypes.Presence,
                room = Key,
                revision = State.Revision,
                participants = State.SnapshotParticipants(),
                lockedNodeIds = State.SnapshotLocks(),
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

        static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
        static readonly TimeSpan SilenceTimeout = TimeSpan.FromSeconds(60);

        /// <summary>
        /// A peer that stops answering is not gone as far as TCP is concerned: a proxy that keeps the
        /// upstream socket open, or a laptop that went to sleep, leaves the receive call parked forever.
        /// The room then stays "occupied" — <c>/rooms</c> hides it, <c>/health</c> counts it, and the
        /// scene cannot be reopened cleanly. So the server asks every 20 s and hangs up after a minute
        /// of silence; aborting the socket makes the receive loop fall out and drop the client.
        /// </summary>
        void StartHeartbeat(WebSocket socket, ClientConnection connection, CancellationToken token)
        {
            Task.Run(async () =>
            {
                var nextPing = DateTime.UtcNow + HeartbeatInterval;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);

                        if (DateTime.UtcNow - connection.LastSeenUtc > SilenceTimeout)
                        {
                            try { socket.Abort(); } catch (Exception) { }
                            return;
                        }

                        // Expire idle leases even when no editor sends anything. This uses the
                        // same operation gate as live writes, joins and recovery control frames.
                        await InOrderAsync(() => Task.CompletedTask, token).ConfigureAwait(false);
                        if (DateTime.UtcNow >= nextPing)
                        {
                            nextPing = DateTime.UtcNow + HeartbeatInterval;
                            await connection
                                .SendAsync(WireJson.Serialize(new WireMessage { type = MessageTypes.Ping, serverTime = Now() }), token)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // normal teardown
                }
                catch (Exception)
                {
                    // A heartbeat that throws must never take the room down with it.
                }
            }, CancellationToken.None);
        }

        Task RemoveAsync(ClientConnection connection)
        {
            return InOrderAsync(() => RemoveCoreAsync(connection), CancellationToken.None);
        }

        async Task RemoveCoreAsync(ClientConnection connection)
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
            await BroadcastPresenceCoreAsync(null, CancellationToken.None).ConfigureAwait(false);
            await BeginSyncCoreAsync().ConfigureAwait(false);
        }

        async Task SendErrorAsync(ClientConnection connection, string code, string message, CancellationToken cancellationToken,
            WireMessage request = null)
        {
            var error = new WireMessage
            {
                type = MessageTypes.Error,
                code = code,
                message = message,
                clientId = connection.ClientId,
                opId = request?.opId,
                syncId = request?.syncId,
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

                    if (result.MessageType != WebSocketMessageType.Text)
                        throw new InvalidOperationException("only text messages are supported");

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

        long Now()
        {
            return _utcNow();
        }
    }
}
