using System;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Encodes the part of a TerrainData that actually changes when someone sculpts or paints it.
    ///
    /// A Terrain is the one scene object that carries no useful data of its own: heights, splat
    /// weights, holes and details all live in the TerrainData <em>asset</em>. The scene op stream
    /// therefore never sees a sculpt — it sees a component whose only interesting field is a GUID
    /// reference to a file that changed on one disk and not the other. So the terrain rides a
    /// separate, region-scoped patch message instead of an op.
    ///
    /// Quantisation is deliberate: a height goes on the wire as a 16-bit sample (1/65535 of full
    /// scale, far below the resolution of anything a Unity Terrain can display) and a splat weight
    /// as one byte. A 1024x1024 heightmap would otherwise cost 4 MB per edit; a 128x128 tile costs
    /// 32 KB.
    /// </summary>
    internal static class TerrainPatchCodec
    {
        /// <summary>Samples per side of a height tile. 128^2 * 2 bytes = 32 KB, ~44 KB of base64.</summary>
        public const int HeightTile = 128;

        /// <summary>Samples per side of a splat tile. Scaled down when a terrain has many layers.</summary>
        public const int SplatTile = 64;

        /// <summary>Largest raw payload a single patch may carry. Keeps one frame comfortably small.</summary>
        public const int MaxPayloadBytes = 32 * 1024;

        public const string KindHeight = "height";
        public const string KindSplat = "splat";

        /// <summary>
        /// The wire's height quantisation, exposed so the sampler can skip samples the wire would carry
        /// identically. Comparing raw floats would report a changed tile for noise the receiver throws
        /// away, and then send a patch with no bytes in it.
        /// </summary>
        public static ushort QuantizeHeight(float value)
        {
            return Quantize(value);
        }

        static ushort Quantize(float value)
        {
            var scaled = Mathf.RoundToInt(Mathf.Clamp(value, 0f, 1f) * 65535f);
            return (ushort)scaled;
        }

        /// <summary>
        /// Bounding box of every sample that differs between two heightmaps, in heightmap space.
        /// Returns false when nothing changed, which is the common case: the dirty flag is asset
        /// wide, so a single painted splat pixel makes the whole heightmap re-read.
        /// </summary>
        public static bool TryDiffHeights(float[,] before, float[,] after, out int x, out int z, out int width, out int height)
        {
            x = 0;
            z = 0;
            width = 0;
            height = 0;

            if (before == null || after == null)
            {
                return false;
            }

            if (before.GetLength(0) != after.GetLength(0) || before.GetLength(1) != after.GetLength(1))
            {
                return false;
            }

            var rows = before.GetLength(0);
            var cols = before.GetLength(1);
            var minX = int.MaxValue;
            var minZ = int.MaxValue;
            var maxX = -1;
            var maxZ = -1;

            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    if (Quantize(before[r, c]) == Quantize(after[r, c]))
                    {
                        continue;
                    }

                    if (c < minX) minX = c;
                    if (c > maxX) maxX = c;
                    if (r < minZ) minZ = r;
                    if (r > maxZ) maxZ = r;
                }
            }

            if (maxX < 0)
            {
                return false;
            }

            x = minX;
            z = minZ;
            width = maxX - minX + 1;
            height = maxZ - minZ + 1;
            return true;
        }

        /// <summary>Bounding box of every changed splat weight, in alphamap space (x, z, layer).</summary>
        public static bool TryDiffSplat(float[,,] before, float[,,] after, out int x, out int z, out int width, out int height)
        {
            x = 0;
            z = 0;
            width = 0;
            height = 0;

            if (before == null || after == null)
            {
                return false;
            }

            if (before.GetLength(0) != after.GetLength(0)
                || before.GetLength(1) != after.GetLength(1)
                || before.GetLength(2) != after.GetLength(2))
            {
                return false;
            }

            var cols = before.GetLength(1);
            var rows = before.GetLength(0);
            var layers = before.GetLength(2);
            var minX = int.MaxValue;
            var minZ = int.MaxValue;
            var maxX = -1;
            var maxZ = -1;

            for (var c = 0; c < cols; c++)
            {
                for (var r = 0; r < rows; r++)
                {
                    var changed = false;
                    for (var l = 0; l < layers; l++)
                    {
                        if (Mathf.RoundToInt(Mathf.Clamp(before[r, c, l], 0f, 1f) * 255f)
                            != Mathf.RoundToInt(Mathf.Clamp(after[r, c, l], 0f, 1f) * 255f))
                        {
                            changed = true;
                            break;
                        }
                    }

                    if (!changed)
                    {
                        continue;
                    }

                    if (c < minX) minX = c;
                    if (c > maxX) maxX = c;
                    if (r < minZ) minZ = r;
                    if (r > maxZ) maxZ = r;
                }
            }

            if (maxX < 0)
            {
                return false;
            }

            x = minX;
            z = minZ;
            width = maxX - minX + 1;
            height = maxZ - minZ + 1;
            return true;
        }

        /// <summary>
        /// Packs a rectangle of a heightmap (indexed <c>[z, x]</c>, Unity's convention) into base64.
        /// Row-major, little-endian, two bytes per sample.
        /// </summary>
        public static string EncodeHeights(float[,] heights, int x, int z, int width, int height)
        {
            if (heights == null || width <= 0 || height <= 0)
            {
                return string.Empty;
            }

            var bytes = new byte[width * height * 2];
            var i = 0;
            for (var dz = 0; dz < height; dz++)
            {
                for (var dx = 0; dx < width; dx++)
                {
                    var q = Quantize(heights[z + dz, x + dx]);
                    bytes[i++] = (byte)(q & 0xFF);
                    bytes[i++] = (byte)(q >> 8);
                }
            }

            return Convert.ToBase64String(bytes);
        }

        /// <summary>Inverse of <see cref="EncodeHeights"/>; the result is <c>[z, x]</c> so SetHeights takes it as is.</summary>
        public static bool TryDecodeHeights(string payload, int width, int height, out float[,] region)
        {
            region = null;

            if (string.IsNullOrEmpty(payload) || width <= 0 || height <= 0)
            {
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                return false;
            }

            if (bytes.Length != width * height * 2)
            {
                return false;
            }

            var values = new float[height, width];
            var i = 0;
            for (var dz = 0; dz < height; dz++)
            {
                for (var dx = 0; dx < width; dx++)
                {
                    var q = (ushort)(bytes[i] | (bytes[i + 1] << 8));
                    i += 2;
                    values[dz, dx] = q / 65535f;
                }
            }

            region = values;
            return true;
        }

        /// <summary>Packs a rectangle of Unity alphamaps (indexed <c>[z, x, layer]</c>) as one byte per weight.</summary>
        public static string EncodeSplat(float[,,] splat, int x, int z, int width, int height, int layers)
        {
            if (splat == null || width <= 0 || height <= 0 || layers <= 0)
            {
                return string.Empty;
            }

            var bytes = new byte[width * height * layers];
            var i = 0;
            for (var dx = 0; dx < width; dx++)
            {
                for (var dz = 0; dz < height; dz++)
                {
                    for (var l = 0; l < layers; l++)
                    {
                        bytes[i++] = (byte)Mathf.RoundToInt(Mathf.Clamp(splat[z + dz, x + dx, l], 0f, 1f) * 255f);
                    }
                }
            }

            return Convert.ToBase64String(bytes);
        }

        /// <summary>Inverse of <see cref="EncodeSplat"/>; the result is <c>[z, x, layer]</c> for SetAlphamaps.</summary>
        public static bool TryDecodeSplat(string payload, int width, int height, int layers, out float[,,] region)
        {
            region = null;

            if (string.IsNullOrEmpty(payload) || width <= 0 || height <= 0 || layers <= 0)
            {
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                return false;
            }

            if (bytes.Length != width * height * layers)
            {
                return false;
            }

            var values = new float[height, width, layers];
            var i = 0;
            for (var dx = 0; dx < width; dx++)
            {
                for (var dz = 0; dz < height; dz++)
                {
                    for (var l = 0; l < layers; l++)
                    {
                        values[dz, dx, l] = bytes[i++] / 255f;
                    }
                }
            }

            region = values;
            return true;
        }

        /// <summary>Tile side that keeps one splat patch under <see cref="MaxPayloadBytes"/>.</summary>
        public static int TileForLayers(int layers)
        {
            if (layers <= 0)
            {
                return SplatTile;
            }

            var tile = SplatTile;
            while (tile > 8 && tile * tile * layers > MaxPayloadBytes)
            {
                tile /= 2;
            }

            return tile;
        }
    }
}
