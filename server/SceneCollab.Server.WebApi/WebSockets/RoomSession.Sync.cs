using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Server.WebApi.WebSockets
{
    public sealed partial class RoomSession
    {
        // All fields and Core helpers in this partial are protected by _operationGate.
        string _syncId;
        bool _syncActive;
        bool _snapshotEndSeen;
        bool _syncEndBroadcast;
        bool _syncFailed;
        int _syncTerrainTiles;
        long _operationTime;
        readonly HashSet<string> _readyClients = new HashSet<string>(StringComparer.Ordinal);

        async Task InOrderAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            await InOrderAsync(async () => { await action().ConfigureAwait(false); return true; }, cancellationToken)
                .ConfigureAwait(false);
        }

        async Task<T> InOrderAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Once state can change, delivery cannot be cancelled by the originating peer.
                // Core helpers never re-enter this gate (including lifecycle and presence).
                // Lease decisions share one linearization time with expiry. Otherwise a lease
                // could expire between the expiry broadcast and validation, disappear inside the
                // registry, and never produce an occupancy update for the other peers.
                _operationTime = Now();
                if (State.TerrainLeases.Expire(_operationTime))
                    await BroadcastTerrainLocksCoreAsync().ConfigureAwait(false);
                return await action().ConfigureAwait(false);
            }
            finally
            {
                _operationGate.Release();
            }
        }

        Task HandleJoinedMessageCoreAsync(ClientConnection connection, WireMessage message)
        {
            var token = CancellationToken.None;
            switch (message.type)
            {
                case MessageTypes.Op:
                    return ApplyAndBroadcastOpAsync(connection, message);
                case MessageTypes.Presence:
                    return State.UpdatePresence(connection.ClientId, message.selectedNodeIds)
                        ? BroadcastPresenceCoreAsync(null, token) : Task.CompletedTask;
                case MessageTypes.Lock:
                    return HandleLockAsync(connection, message, false, token);
                case MessageTypes.Unlock:
                    return HandleLockAsync(connection, message, true, token);
                case MessageTypes.Assets:
                    return HandleAssetsAsync(connection, message, token);
                case MessageTypes.View:
                    return HandleViewAsync(connection, message, token);
                case MessageTypes.Terrain:
                    return HandleTerrainCoreAsync(connection, message);
                case MessageTypes.TerrainLock:
                    return HandleTerrainLockCoreAsync(connection, message, false);
                case MessageTypes.TerrainUnlock:
                    return HandleTerrainLockCoreAsync(connection, message, true);
                case MessageTypes.SyncRequest:
                    return BeginSyncCoreAsync();
                case MessageTypes.SyncEnd:
                    return HandleSyncEndCoreAsync(connection, message);
                case MessageTypes.SyncReady:
                    return HandleSyncReadyCoreAsync(connection, message);
                default:
                    return SendErrorAsync(connection, ErrorCodes.Malformed, "unexpected type " + message.type, token, message);
            }
        }

        async Task BeginSyncCoreAsync()
        {
            _syncId = Guid.NewGuid().ToString("N");
            _syncActive = State.ParticipantCount > 0;
            _snapshotEndSeen = _syncEndBroadcast = _syncFailed = false;
            _syncTerrainTiles = 0;
            _readyClients.Clear();
            State.TerrainLeases.Clear();
            if (!_syncActive)
            {
                _assetCount = 0;
                _assetGuids = Array.Empty<string>();
                return;
            }

            await BroadcastAsync(WireJson.Serialize(new WireMessage
            {
                type = MessageTypes.SyncBegin,
                syncId = _syncId,
                clientId = State.HostClientId,
                isHost = true,
                room = Key,
                revision = State.Revision,
                serverTime = Now(),
            }), null, CancellationToken.None).ConfigureAwait(false);
            await BroadcastTerrainLocksCoreAsync().ConfigureAwait(false);
        }

        string ValidateSnapshotSource(ClientConnection connection, WireMessage message, bool snapshot)
        {
            if (snapshot)
            {
                if (!string.Equals(State.HostClientId, connection.ClientId, StringComparison.Ordinal))
                    return ErrorCodes.NotHost;
                if (!_syncActive || !string.Equals(message.syncId, _syncId, StringComparison.Ordinal))
                    return ErrorCodes.SyncMismatch;
                if (_syncFailed) return ErrorCodes.SyncFailed;
                if (_syncEndBroadcast) return ErrorCodes.SyncIncomplete;
                return null;
            }
            return _syncActive ? ErrorCodes.SyncInProgress : null;
        }

        static string SyncRejectionMessage(string code)
        {
            switch (code)
            {
                case ErrorCodes.NotHost: return "only the current host supplies snapshots and sync_end";
                case ErrorCodes.SyncMismatch: return "syncId does not identify an active recovery epoch";
                case ErrorCodes.SyncIncomplete: return "recovery phase or snapshot terrain count does not match";
                case ErrorCodes.SyncFailed: return "a peer failed recovery; send sync_request to start a new epoch";
                default: return "room recovery is in progress";
            }
        }

        async Task HandleSyncEndCoreAsync(ClientConnection connection, WireMessage message)
        {
            var code = ValidateSnapshotSource(connection, message, true);
            if (code == null && (!_snapshotEndSeen || message.syncTerrainTiles < 0
                || message.syncTerrainTiles != _syncTerrainTiles))
                code = ErrorCodes.SyncIncomplete;

            if (code != null)
            {
                await SendErrorAsync(connection, code, SyncRejectionMessage(code), CancellationToken.None, message)
                    .ConfigureAwait(false);
                return;
            }

            _syncEndBroadcast = true;
            await BroadcastAsync(WireJson.Serialize(new WireMessage
            {
                type = MessageTypes.SyncEnd,
                syncId = _syncId,
                syncTerrainTiles = _syncTerrainTiles,
                clientId = connection.ClientId,
                isHost = true,
                accepted = true,
                room = Key,
                revision = State.Revision,
                serverTime = Now(),
            }), null, CancellationToken.None).ConfigureAwait(false);
        }

        async Task HandleSyncReadyCoreAsync(ClientConnection connection, WireMessage message)
        {
            string code = null;
            var reportToRoom = false;
            if (!_syncActive || !string.Equals(message.syncId, _syncId, StringComparison.Ordinal))
                code = ErrorCodes.SyncMismatch;
            else if (!message.accepted)
            {
                _syncFailed = true;
                _readyClients.Clear();
                code = ErrorCodes.SyncFailed;
                reportToRoom = true;
            }
            else if (_syncFailed)
                code = ErrorCodes.SyncFailed;
            else if (!_syncEndBroadcast)
                code = ErrorCodes.SyncIncomplete;

            if (code != null)
            {
                var failure = WireJson.Serialize(new WireMessage
                {
                    type = MessageTypes.SyncReady,
                    syncId = message.syncId,
                    opId = message.opId,
                    accepted = false,
                    clientId = connection.ClientId,
                    code = code,
                    message = reportToRoom ? PeerRecoveryFailure(connection, message.message) : SyncRejectionMessage(code),
                    room = Key,
                    revision = State.Revision,
                    serverTime = Now(),
                });
                if (reportToRoom)
                    await BroadcastAsync(failure, null, CancellationToken.None).ConfigureAwait(false);
                else
                    await connection.SendAsync(failure, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            // clientId is always the authenticated socket identity, including the host's vote.
            _readyClients.Add(connection.ClientId);
            foreach (var participant in State.SnapshotParticipants())
                if (!_readyClients.Contains(participant.clientId)) return;

            _syncActive = false;
            await BroadcastAsync(WireJson.Serialize(new WireMessage
            {
                type = MessageTypes.SyncReady,
                syncId = _syncId,
                accepted = true,
                clientId = State.HostClientId,
                isHost = true,
                room = Key,
                revision = State.Revision,
                serverTime = Now(),
            }), null, CancellationToken.None).ConfigureAwait(false);
        }

        static string PeerRecoveryFailure(ClientConnection connection, string reported)
        {
            var name = PlainFailureText(connection.DisplayName, 64);
            var clientId = PlainFailureText(connection.ClientId, 64);
            var reason = PlainFailureText(reported, 512);
            if (reason.Length == 0) reason = "peer reported unsuccessful recovery";
            return "Recovery failed at " + name + " (" + clientId + "): " + reason;
        }

        static string PlainFailureText(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var text = new StringBuilder(Math.Min(value.Length, limit));
            foreach (var c in value)
            {
                if (text.Length == limit) break;
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    if (text.Length > 0 && text[text.Length - 1] != ' ') text.Append(' ');
                }
                else text.Append(c);
            }
            // Do not split a UTF-16 character when truncating an otherwise valid report.
            if (text.Length > 0 && char.IsHighSurrogate(text[text.Length - 1])) text.Length--;
            return text.ToString().Trim();
        }
    }
}
