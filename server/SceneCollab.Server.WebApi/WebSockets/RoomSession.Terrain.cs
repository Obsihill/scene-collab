using System;
using System.Threading;
using System.Threading.Tasks;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Server.WebApi.WebSockets
{
    public sealed partial class RoomSession
    {
        const int MaxTerrainPayloadChars = 64 * 1024;
        const int MaxTerrainPayloadBytes = MaxTerrainPayloadChars / 4 * 3;

        // These helpers run only while _operationGate is held.
        async Task HandleTerrainLockCoreAsync(ClientConnection connection, WireMessage message, bool release)
        {
            string code = null;
            TerrainRegionLease lease = null;
            if (string.IsNullOrWhiteSpace(message.opId) || string.IsNullOrWhiteSpace(message.terrainGuid))
                code = ErrorCodes.Malformed;
            else if (_syncActive && !release)
                code = ErrorCodes.SyncInProgress;
            else if (release)
            {
                if (!State.TerrainLeases.TryRelease(connection.ClientId, message.terrainGuid, message.terrainLeaseId, _operationTime))
                    code = ErrorCodes.TerrainLeaseInvalid;
            }
            else
            {
                State.TerrainLeases.TryAcquire(connection.ClientId, connection.DisplayName, message.terrainGuid,
                    message.terrainLeaseId, message.regionX, message.regionZ, message.regionWidth, message.regionHeight,
                    _operationTime, out lease, out code);
            }

            var reply = new WireMessage
            {
                type = MessageTypes.TerrainLocks,
                opId = message.opId,
                syncId = message.syncId,
                accepted = code == null,
                code = code,
                message = code == null ? null : "terrain lease request rejected: " + code,
                clientId = connection.ClientId,
                displayName = connection.DisplayName,
                terrainGuid = message.terrainGuid,
                terrainLeaseId = lease?.leaseId ?? message.terrainLeaseId,
                terrainLocks = State.TerrainLeases.Snapshot(),
                room = Key,
                revision = State.Revision,
                serverTime = Now(),
            };
            var json = WireJson.Serialize(reply);
            if (code == null)
                await BroadcastAsync(json, null, CancellationToken.None).ConfigureAwait(false);
            else
                await connection.SendAsync(json, CancellationToken.None).ConfigureAwait(false);
        }

        Task BroadcastTerrainLocksCoreAsync()
        {
            return BroadcastAsync(WireJson.Serialize(new WireMessage
            {
                type = MessageTypes.TerrainLocks,
                accepted = true,
                terrainLocks = State.TerrainLeases.Snapshot(),
                room = Key,
                revision = State.Revision,
                serverTime = Now(),
            }), null, CancellationToken.None);
        }

        async Task HandleTerrainCoreAsync(ClientConnection connection, WireMessage message)
        {
            var code = ValidateSnapshotSource(connection, message, message.snapshot);
            int x = 0, z = 0, width = 0, height = 0;
            if (code == null && !ValidateTerrainPatch(message, out x, out z, out width, out height))
                code = ErrorCodes.Malformed;
            if (code == null && message.snapshot && _syncTerrainTiles == int.MaxValue)
                code = ErrorCodes.Malformed;
            if (code == null && !message.snapshot
                && !State.TerrainLeases.TryRenew(connection.ClientId, message.terrainGuid, message.terrainLeaseId,
                    x, z, width, height, _operationTime))
                code = ErrorCodes.TerrainLeaseInvalid;

            var ack = new WireMessage
            {
                type = MessageTypes.TerrainAck,
                opId = message.opId,
                syncId = message.syncId,
                terrainLeaseId = message.terrainLeaseId,
                clientId = connection.ClientId,
                accepted = code == null,
                code = code,
                message = code == null ? null : "terrain patch rejected: " + code,
                room = Key,
                revision = State.Revision,
                serverTime = Now(),
            };
            await connection.SendAsync(WireJson.Serialize(ack), CancellationToken.None).ConfigureAwait(false);
            if (code != null) return;

            if (message.snapshot) _syncTerrainTiles++;
            var broadcast = new WireMessage
            {
                type = MessageTypes.Terrain,
                opId = message.opId,
                clientId = connection.ClientId,
                displayName = connection.DisplayName,
                accepted = true,
                room = Key,
                revision = State.Revision,
                snapshot = message.snapshot,
                syncId = message.syncId,
                terrainLeaseId = message.terrainLeaseId,
                terrainValid = true,
                terrainKind = message.terrainKind,
                terrainGuid = message.terrainGuid,
                terrainResolution = message.terrainResolution,
                terrainLayers = message.terrainLayers,
                terrainX = message.terrainX,
                terrainZ = message.terrainZ,
                terrainWidth = message.terrainWidth,
                terrainHeight = message.terrainHeight,
                terrainPayload = message.terrainPayload,
                serverTime = Now(),
            };
            await BroadcastAsync(WireJson.Serialize(broadcast), connection.ClientId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!message.snapshot)
                await BroadcastTerrainLocksCoreAsync().ConfigureAwait(false);
        }

        static bool ValidateTerrainPatch(WireMessage message, out int x, out int z, out int width, out int height)
        {
            x = z = width = height = 0;
            if (!message.terrainValid || string.IsNullOrWhiteSpace(message.opId)
                || string.IsNullOrWhiteSpace(message.terrainGuid)
                || !TerrainRegionRules.TryMapPatch(message.terrainResolution, message.terrainX, message.terrainZ,
                    message.terrainWidth, message.terrainHeight, out x, out z, out width, out height)
                || string.IsNullOrEmpty(message.terrainPayload) || message.terrainPayload.Length > MaxTerrainPayloadChars)
                return false;

            int bytesPerSample;
            if (message.terrainKind == "height" && message.terrainLayers == 0)
                bytesPerSample = 2;
            else if (message.terrainKind == "splat" && message.terrainLayers > 0)
                bytesPerSample = message.terrainLayers;
            else
                return false;

            // Divide before multiplying by the layer count: even long would overflow on three
            // attacker-controlled int dimensions. Allocation is bounded independently of them.
            var samples = (long)message.terrainWidth * message.terrainHeight;
            if (samples > MaxTerrainPayloadBytes / bytesPerSample) return false;
            var expectedBytes = (int)(samples * bytesPerSample);
            if (message.terrainPayload.Length != (expectedBytes + 2) / 3 * 4) return false;
            var bytes = new byte[expectedBytes];
            return Convert.TryFromBase64String(message.terrainPayload, bytes, out var written)
                && written == expectedBytes;
        }
    }
}
