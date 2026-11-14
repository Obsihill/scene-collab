namespace SceneCollab.Contracts.Protocol
{
    /// <summary>Shared, Unity-independent bounds and conservative sample-to-sector mapping.</summary>
    public static class TerrainRegionRules
    {
        public const int GridSize = 16;
        public const int LeaseDurationMs = 15000;

        public static bool IsValid(int x, int z, int width, int height)
        {
            return x >= 0 && z >= 0 && x < GridSize && z < GridSize
                && width > 0 && height > 0 && width <= GridSize - x && height <= GridSize - z;
        }

        public static bool TryMapPatch(int resolution, int x, int z, int width, int height,
            out int regionX, out int regionZ, out int regionWidth, out int regionHeight)
        {
            regionX = regionZ = regionWidth = regionHeight = 0;
            if (resolution <= 0 || x < 0 || z < 0 || x >= resolution || z >= resolution
                || width <= 0 || height <= 0 || width > resolution - x || height > resolution - z)
                return false;

            // Cover the whole half-open sample rectangle. Long arithmetic also accepts large
            // resolutions safely; ceiling the far edge includes samples straddling a sector edge.
            regionX = (int)((long)x * GridSize / resolution);
            regionZ = (int)((long)z * GridSize / resolution);
            var endX = (int)((((long)x + width) * GridSize + resolution - 1) / resolution);
            var endZ = (int)((((long)z + height) * GridSize + resolution - 1) / resolution);
            regionWidth = endX - regionX;
            regionHeight = endZ - regionZ;
            return true;
        }

        public static bool Overlaps(int x, int z, int width, int height, TerrainRegionLease other)
        {
            return x < other.x + other.width && other.x < x + width
                && z < other.z + other.height && other.z < z + height;
        }

        public static bool Covers(TerrainRegionLease lease, int x, int z, int width, int height)
        {
            return lease != null && IsValid(x, z, width, height)
                && lease.x <= x && lease.z <= z
                && lease.x + lease.width >= x + width && lease.z + lease.height >= z + height;
        }
    }
}
