using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    // The retained arrays contain only acknowledged samples. Captures and wire tiles never
    // mutate them; this is also the source used to undo unauthorized brush/script writes.
    internal sealed class TerrainBaseline
    {
        public readonly string Guid;
        public readonly TerrainData Data;
        public readonly int HeightResolution, AlphaResolution, Layers;
        public readonly float[,] Heights;
        public readonly float[,,] Splat;
        public int DirtyCount;
        public uint HeightVersion, AlphaVersion;
        public int AuditCursor;
        public bool AuditRequested;

        // Unity's GetHeights/GetAlphamaps allocate, but our rollback buffers can be reused.
        float[,] _heightBuffer;
        float[,,] _splatBuffer;

        public TerrainBaseline(string guid, TerrainData data)
        {
            Guid = guid;
            Data = data;
            HeightResolution = data.heightmapResolution;
            AlphaResolution = data.alphamapResolution;
            Layers = data.alphamapLayers;
            Heights = data.GetHeights(0, 0, HeightResolution, HeightResolution);
            if (Layers > 0 && data.alphamapTextureCount > 0)
                Splat = data.GetAlphamaps(0, 0, AlphaResolution, AlphaResolution);
        }

        public bool MatchesLayout => Data != null && HeightResolution == Data.heightmapResolution
            && AlphaResolution == Data.alphamapResolution && Layers == Data.alphamapLayers;

        public int Resolution(string kind) => kind == TerrainPatchCodec.KindHeight ? HeightResolution : AlphaResolution;

        public int TileSize(string kind) => kind == TerrainPatchCodec.KindHeight
            ? TerrainPatchCodec.HeightTile : TerrainPatchCodec.TileForLayers(Layers);

        public bool Diff(string kind, RectInt area, float[,] heights, float[,,] splat, out RectInt changed)
        {
            var minX = area.width;
            var minZ = area.height;
            var maxX = -1;
            var maxZ = -1;
            for (var z = 0; z < area.height; z++)
            for (var x = 0; x < area.width; x++)
            {
                var different = false;
                if (kind == TerrainPatchCodec.KindHeight)
                    different = TerrainPatchCodec.QuantizeHeight(Heights[area.y + z, area.x + x])
                        != TerrainPatchCodec.QuantizeHeight(heights[z, x]);
                else
                    for (var layer = 0; layer < Layers; layer++)
                        if (Mathf.RoundToInt(Splat[area.y + z, area.x + x, layer] * 255f)
                            != Mathf.RoundToInt(splat[z, x, layer] * 255f)) { different = true; break; }
                if (!different) continue;
                minX = Math.Min(minX, x); minZ = Math.Min(minZ, z);
                maxX = Math.Max(maxX, x); maxZ = Math.Max(maxZ, z);
            }
            changed = maxX < 0 ? default : new RectInt(minX, minZ, maxX - minX + 1, maxZ - minZ + 1);
            return maxX >= 0;
        }

        public List<TerrainCapturedPatch> Capture(string kind, RectInt region, ref int tilesRead)
        {
            var result = new List<TerrainCapturedPatch>();
            var tile = TileSize(kind);
            for (var z = region.y; z < region.yMax; z += tile)
            for (var x = region.x; x < region.xMax; x += tile)
            {
                var area = new RectInt(x, z, Math.Min(tile, region.xMax - x), Math.Min(tile, region.yMax - z));
                float[,] heights = null;
                float[,,] splat = null;
                if (kind == TerrainPatchCodec.KindHeight) heights = Data.GetHeights(x, z, area.width, area.height);
                else splat = Data.GetAlphamaps(x, z, area.width, area.height);
                tilesRead++;
                if (!Diff(kind, area, heights, splat, out var changed)) continue;
                var message = CreateMessage(kind, new RectInt(x + changed.x, z + changed.y, changed.width, changed.height));
                message.terrainPayload = kind == TerrainPatchCodec.KindHeight
                    ? TerrainPatchCodec.EncodeHeights(heights, changed.x, changed.y, changed.width, changed.height)
                    : TerrainPatchCodec.EncodeSplat(splat, changed.x, changed.y, changed.width, changed.height, Layers);
                if (!TerrainCapturedPatch.TryDecode(message, out var patch))
                    throw new InvalidOperationException("terrain capture exceeded the wire tile limit");
                result.Add(patch);
            }
            return result;
        }

        public WireMessage CreateMessage(string kind, RectInt area)
        {
            return new WireMessage
            {
                type = MessageTypes.Terrain, terrainValid = true, terrainGuid = Guid,
                terrainKind = kind, terrainResolution = Resolution(kind),
                terrainLayers = kind == TerrainPatchCodec.KindSplat ? Layers : 0,
                terrainX = area.x, terrainZ = area.y, terrainWidth = area.width, terrainHeight = area.height,
                opId = System.Guid.NewGuid().ToString("N"),
            };
        }

        public IEnumerable<WireMessage> Snapshot(string syncId)
        {
            foreach (var kind in new[] { TerrainPatchCodec.KindHeight, TerrainPatchCodec.KindSplat })
            {
                if (kind == TerrainPatchCodec.KindSplat && Splat == null) continue;
                var resolution = Resolution(kind);
                var tile = TileSize(kind);
                for (var z = 0; z < resolution; z += tile)
                for (var x = 0; x < resolution; x += tile)
                {
                    if (!MatchesLayout) throw new InvalidOperationException("terrain layout changed during recovery");
                    var area = new RectInt(x, z, Math.Min(tile, resolution - x), Math.Min(tile, resolution - z));
                    var message = CreateMessage(kind, area);
                    message.snapshot = true;
                    message.syncId = syncId;
                    message.terrainPayload = kind == TerrainPatchCodec.KindHeight
                        ? TerrainPatchCodec.EncodeHeights(Heights, x, z, area.width, area.height)
                        : TerrainPatchCodec.EncodeSplat(Splat, x, z, area.width, area.height, Layers);
                    yield return message;
                }
            }
        }

        // Call only inside TerrainSync's callback guard. Retain Unity's actual CPU representation
        // after a write, since the engine may quantize the decoded values a second time.
        public void Commit(TerrainCapturedPatch patch)
        {
            var area = patch.Area;
            if (patch.Heights != null)
            {
                var samples = Data.GetHeights(area.x, area.y, area.width, area.height);
                for (var z = 0; z < area.height; z++)
                for (var x = 0; x < area.width; x++) Heights[area.y + z, area.x + x] = samples[z, x];
            }
            else
            {
                var samples = Data.GetAlphamaps(area.x, area.y, area.width, area.height);
                for (var z = 0; z < area.height; z++)
                for (var x = 0; x < area.width; x++)
                for (var l = 0; l < Layers; l++) Splat[area.y + z, area.x + x, l] = samples[z, x, l];
            }
        }

        public void Restore(string kind, RectInt region)
        {
            if (!MatchesLayout) throw new InvalidOperationException("terrain layout changed; rollback requires recovery");
            var tile = TileSize(kind);
            for (var z = region.y; z < region.yMax; z += tile)
            for (var x = region.x; x < region.xMax; x += tile)
            {
                var width = Math.Min(tile, region.xMax - x);
                var height = Math.Min(tile, region.yMax - z);
                if (kind == TerrainPatchCodec.KindHeight)
                {
                    if (_heightBuffer == null || _heightBuffer.GetLength(0) != height || _heightBuffer.GetLength(1) != width)
                        _heightBuffer = new float[height, width];
                    for (var r = 0; r < height; r++)
                    for (var c = 0; c < width; c++) _heightBuffer[r, c] = Heights[z + r, x + c];
                    Data.SetHeights(x, z, _heightBuffer);
                }
                else if (Splat != null)
                {
                    if (_splatBuffer == null || _splatBuffer.GetLength(0) != height || _splatBuffer.GetLength(1) != width)
                        _splatBuffer = new float[height, width, Layers];
                    for (var r = 0; r < height; r++)
                    for (var c = 0; c < width; c++)
                    for (var l = 0; l < Layers; l++) _splatBuffer[r, c, l] = Splat[z + r, x + c, l];
                    Data.SetAlphamaps(x, z, _splatBuffer);
                }
            }
        }
    }

    internal sealed class TerrainCapturedPatch
    {
        public WireMessage Message;
        public float[,] Heights;
        public float[,,] Splat;
        public RectInt Area => new RectInt(Message.terrainX, Message.terrainZ, Message.terrainWidth, Message.terrainHeight);

        public void Write(TerrainData data)
        {
            if (Heights != null) data.SetHeights(Message.terrainX, Message.terrainZ, Heights);
            else data.SetAlphamaps(Message.terrainX, Message.terrainZ, Splat);
        }

        public static bool TryDecode(WireMessage message, out TerrainCapturedPatch patch)
        {
            patch = null;
            if (message == null || !message.terrainValid || string.IsNullOrEmpty(message.terrainGuid)
                || !TerrainRegionRules.TryMapPatch(message.terrainResolution, message.terrainX, message.terrainZ,
                    message.terrainWidth, message.terrainHeight, out _, out _, out _, out _)) return false;
            var height = message.terrainKind == TerrainPatchCodec.KindHeight;
            if (!height && (message.terrainKind != TerrainPatchCodec.KindSplat || message.terrainLayers <= 0)) return false;
            var bytes = (long)message.terrainWidth * message.terrainHeight * (height ? 2 : message.terrainLayers);
            if (bytes > TerrainPatchCodec.MaxPayloadBytes || string.IsNullOrEmpty(message.terrainPayload)
                || message.terrainPayload.Length > (TerrainPatchCodec.MaxPayloadBytes + 2) / 3 * 4) return false;
            var result = new TerrainCapturedPatch { Message = message };
            if (height
                ? !TerrainPatchCodec.TryDecodeHeights(message.terrainPayload, message.terrainWidth, message.terrainHeight, out result.Heights)
                : !TerrainPatchCodec.TryDecodeSplat(message.terrainPayload, message.terrainWidth, message.terrainHeight,
                    message.terrainLayers, out result.Splat)) return false;
            patch = result;
            return true;
        }
    }
}
