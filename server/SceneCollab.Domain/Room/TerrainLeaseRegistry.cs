using System;
using System.Collections.Generic;
using System.Linq;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Domain.Room
{
    /// <summary>Atomic rectangular terrain leases. Clock input is UTC Unix milliseconds.</summary>
    public sealed class TerrainLeaseRegistry
    {
        readonly object _gate = new object();
        readonly Dictionary<string, TerrainRegionLease> _leases =
            new Dictionary<string, TerrainRegionLease>(StringComparer.Ordinal);

        public bool TryAcquire(string clientId, string displayName, string terrainGuid, string leaseId,
            int x, int z, int width, int height, long now, out TerrainRegionLease lease, out string code)
        {
            lease = null;
            code = ErrorCodes.Malformed;
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(terrainGuid)
                || !TerrainRegionRules.IsValid(x, z, width, height))
                return false;

            lock (_gate)
            {
                ExpireCore(now);
                TerrainRegionLease existing = null;
                if (!string.IsNullOrEmpty(leaseId))
                {
                    if (!_leases.TryGetValue(leaseId, out existing)
                        || !SameOwner(existing, clientId, terrainGuid))
                    {
                        code = ErrorCodes.TerrainLeaseInvalid;
                        return false;
                    }

                    // An expansion never gives up the old rectangle, and either gets the whole
                    // bounding rectangle or leaves the original lease entirely unchanged.
                    var right = Math.Max(x + width, existing.x + existing.width);
                    var top = Math.Max(z + height, existing.z + existing.height);
                    x = Math.Min(x, existing.x);
                    z = Math.Min(z, existing.z);
                    width = right - x;
                    height = top - z;
                }

                foreach (var other in _leases.Values)
                {
                    if (string.Equals(other.terrainGuid, terrainGuid, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(other.clientId, clientId, StringComparison.Ordinal)
                        && TerrainRegionRules.Overlaps(x, z, width, height, other))
                    {
                        code = ErrorCodes.Locked;
                        return false;
                    }
                }

                var granted = new TerrainRegionLease
                {
                    terrainGuid = terrainGuid,
                    leaseId = existing?.leaseId ?? Guid.NewGuid().ToString("N"),
                    clientId = clientId,
                    displayName = displayName,
                    x = x, z = z, width = width, height = height,
                    expiresAt = now + TerrainRegionRules.LeaseDurationMs,
                };
                _leases[granted.leaseId] = granted;
                lease = Clone(granted);
                code = null;
                return true;
            }
        }

        public bool TryRenew(string clientId, string terrainGuid, string leaseId,
            int x, int z, int width, int height, long now)
        {
            lock (_gate)
            {
                ExpireCore(now);
                if (string.IsNullOrEmpty(leaseId) || !_leases.TryGetValue(leaseId, out var lease)
                    || !SameOwner(lease, clientId, terrainGuid)
                    || !TerrainRegionRules.Covers(lease, x, z, width, height))
                    return false;

                lease.expiresAt = now + TerrainRegionRules.LeaseDurationMs;
                return true;
            }
        }

        public bool TryRelease(string clientId, string terrainGuid, string leaseId, long now)
        {
            lock (_gate)
            {
                ExpireCore(now);
                return !string.IsNullOrEmpty(leaseId) && _leases.TryGetValue(leaseId, out var lease)
                    && SameOwner(lease, clientId, terrainGuid) && _leases.Remove(leaseId);
            }
        }

        public bool ReleaseClient(string clientId)
        {
            lock (_gate)
            {
                var keys = _leases.Values.Where(lease => lease.clientId == clientId)
                    .Select(lease => lease.leaseId).ToArray();
                foreach (var key in keys) _leases.Remove(key);
                return keys.Length > 0;
            }
        }

        public bool Expire(long now)
        {
            lock (_gate) { return ExpireCore(now); }
        }

        public void Clear()
        {
            lock (_gate) { _leases.Clear(); }
        }

        public TerrainRegionLease[] Snapshot()
        {
            lock (_gate)
            {
                return _leases.Values.OrderBy(lease => lease.leaseId, StringComparer.Ordinal)
                    .Select(Clone).ToArray();
            }
        }

        bool ExpireCore(long now)
        {
            var keys = _leases.Values.Where(lease => lease.expiresAt <= now)
                .Select(lease => lease.leaseId).ToArray();
            foreach (var key in keys) _leases.Remove(key);
            return keys.Length > 0;
        }

        static bool SameOwner(TerrainRegionLease lease, string clientId, string terrainGuid)
        {
            return string.Equals(lease.clientId, clientId, StringComparison.Ordinal)
                && string.Equals(lease.terrainGuid, terrainGuid, StringComparison.OrdinalIgnoreCase);
        }

        static TerrainRegionLease Clone(TerrainRegionLease lease)
        {
            return new TerrainRegionLease
            {
                terrainGuid = lease.terrainGuid, leaseId = lease.leaseId,
                clientId = lease.clientId, displayName = lease.displayName,
                x = lease.x, z = lease.z, width = lease.width, height = lease.height,
                expiresAt = lease.expiresAt,
            };
        }
    }
}
