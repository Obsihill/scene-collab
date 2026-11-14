using System;

namespace SceneCollab.Contracts.Protocol
{
    [Serializable]
    public sealed class TerrainRegionLease
    {
        public string terrainGuid;
        public string leaseId;
        public string clientId;
        public string displayName;
        public int x;
        public int z;
        public int width;
        public int height;

        /// <summary>UTC Unix milliseconds; a patch renews its lease for 15 seconds.</summary>
        public long expiresAt;
    }
}
