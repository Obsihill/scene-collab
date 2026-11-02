using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Keeps the terrain in a room identical on every editor, without putting a single heightmap in
    /// the op stream.
    ///
    /// Why this is not an op: a Terrain component is a pointer. The heights, splat weights and holes
    /// live in the TerrainData asset, so sculpting changes a file on the host's disk and nothing in
    /// the scene. The ops layer diffs components, sees an unchanged reference, and correctly reports
    /// no change. The only honest fix is a separate, explicitly scoped patch channel, which is what
    /// this class speaks.
    ///
    /// Why it is host-authoritative: a terrain is one asset shared by the whole room, and unlike a
    /// scene object it cannot be locked per node without inventing per-sample ownership. The host
    /// (the editor that opened the scene) is the only sculptor; guests apply. A guest that sculpts
    /// keeps its result locally and is overwritten by the host's next patch — the same rule the
    /// asset manifest already follows.
    ///
    /// Why dirty-flag gated: reading a 1024x1024 heightmap costs 4 MB of managed allocation, and
    /// reading alphamaps costs 16 MB. Both are affordable once per edit and ruinous at 10 Hz, so
    /// polling only starts after Unity marks the TerrainData dirty and the comparison itself is
    /// quantised exactly like the wire format — no change, no traffic.
    /// </summary>
    internal sealed class TerrainSync
    {
        /// <summary>How often the dirty flag is checked. Sculpt strokes are far coarser than 10 Hz.</summary>
        public const double PollSeconds = 0.6;

        /// <summary>Alphamaps are read at most this often; painting is cheaper to see than to measure.</summary>
        public const double SplatPollSeconds = 2.0;

        sealed class Baseline
        {
            public int HeightResolution;
            public int AlphaResolution;
            public int Layers;
            public float[,] Heights;
            public float[,,] Splat;
            public double LastSplatRead;
        }

        readonly Dictionary<string, Baseline> _baselines = new Dictionary<string, Baseline>(StringComparer.Ordinal);
        readonly Dictionary<string, TerrainData> _byGuid = new Dictionary<string, TerrainData>(StringComparer.Ordinal);
        readonly List<TerrainData> _terrains = new List<TerrainData>();

        ISceneCollabSession _session;
        double _nextPoll;

        /// <summary>Patches this editor sent (host side).</summary>
        public int Sent { get; private set; }

        /// <summary>Patches this editor applied (guest side).</summary>
        public int Applied { get; private set; }

        /// <summary>Patches that could not be applied: missing terrain, resolution or layer mismatch.</summary>
        public int Skipped { get; private set; }

        /// <summary>Terrains whose data has no asset GUID (created at runtime, never saved).</summary>
        public int Unsupported { get; private set; }

        /// <summary>Terrains currently in the session, as far as this editor can see.</summary>
        public int KnownTerrains
        {
            get { return _byGuid.Count; }
        }

        public string LastNote { get; private set; }

        public void Bind(ISceneCollabSession session)
        {
            if (ReferenceEquals(_session, session))
            {
                return;
            }

            Unbind();

            _session = session;
            if (_session != null)
            {
                _session.TerrainReceived += OnTerrainReceived;
            }
        }

        public void Unbind()
        {
            if (_session != null)
            {
                _session.TerrainReceived -= OnTerrainReceived;
            }

            _session = null;
            Reset();
        }

        /// <summary>Drops every baseline. Called when syncing starts, stops, or the scene changes.</summary>
        public void Reset()
        {
            _baselines.Clear();
            _byGuid.Clear();
            _terrains.Clear();
            _nextPoll = 0;
        }

        public void Tick()
        {
            if (_session == null)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (now < _nextPoll)
            {
                return;
            }

            _nextPoll = now + PollSeconds;

            RefreshTerrainMap();

            if (!_session.IsHost)
            {
                // Host authoritative: a guest that sculpts would fight a copy of the same asset it
                // can never win, and every patch it received would be undone by its own edit.
                return;
            }

            for (var i = 0; i < _terrains.Count; i++)
            {
                Poll(_terrains[i], now);
            }
        }

        void Poll(TerrainData data, double now)
        {
            if (data == null)
            {
                return;
            }

            string guid;
            long localId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data, out guid, out localId) || string.IsNullOrEmpty(guid))
            {
                Unsupported++;
                LastNote = "terrain has no asset GUID";
                return;
            }

            Baseline baseline;
            if (!_baselines.TryGetValue(guid, out baseline))
            {
                baseline = new Baseline
                {
                    HeightResolution = data.heightmapResolution,
                    AlphaResolution = data.alphamapResolution,
                    Layers = data.alphamapLayers,
                };
                CaptureHeights(data, baseline);
                _baselines[guid] = baseline;
                return;
            }

            if (baseline.HeightResolution != data.heightmapResolution
                || baseline.AlphaResolution != data.alphamapResolution
                || baseline.Layers != data.alphamapLayers)
            {
                // The terrain's resolution or layer count changed: every prior sample is meaningless.
                baseline.HeightResolution = data.heightmapResolution;
                baseline.AlphaResolution = data.alphamapResolution;
                baseline.Layers = data.alphamapLayers;
                CaptureHeights(data, baseline);
                baseline.Splat = null;
                LastNote = "terrain settings changed; rebaselined";
                return;
            }

            if (!EditorUtility.IsDirty(data))
            {
                return;
            }

            var heights = data.GetHeights(0, 0, baseline.HeightResolution, baseline.HeightResolution);
            int x, z, width, height;
            if (TerrainPatchCodec.TryDiffHeights(baseline.Heights, heights, out x, out z, out width, out height))
            {
                EmitHeights(guid, baseline.HeightResolution, heights, x, z, width, height);
            }

            baseline.Heights = heights;

            if (baseline.Layers > 0
                && now - baseline.LastSplatRead >= SplatPollSeconds
                && data.alphamapTextureCount > 0)
            {
                baseline.LastSplatRead = now;
                var splat = data.GetAlphamaps(0, 0, baseline.AlphaResolution, baseline.AlphaResolution);
                if (baseline.Splat != null
                    && TerrainPatchCodec.TryDiffSplat(baseline.Splat, splat, out x, out z, out width, out height))
                {
                    EmitSplat(guid, baseline.AlphaResolution, splat, baseline.Layers, x, z, width, height);
                }

                baseline.Splat = splat;
            }
        }

        void CaptureHeights(TerrainData data, Baseline baseline)
        {
            baseline.Heights = data.GetHeights(0, 0, baseline.HeightResolution, baseline.HeightResolution);
            baseline.Splat = null;
            baseline.LastSplatRead = 0;
        }

        void EmitHeights(string guid, int resolution, float[,] heights, int x, int z, int width, int height)
        {
            const int tile = TerrainPatchCodec.HeightTile;
            for (var tz = z; tz < z + height; tz += tile)
            {
                for (var tx = x; tx < x + width; tx += tile)
                {
                    var tw = Mathf.Min(tile, x + width - tx);
                    var th = Mathf.Min(tile, z + height - tz);
                    var message = new WireMessage
                    {
                        type = MessageTypes.Terrain,
                        terrainValid = true,
                        terrainKind = TerrainPatchCodec.KindHeight,
                        terrainGuid = guid,
                        terrainResolution = resolution,
                        terrainX = tx,
                        terrainZ = tz,
                        terrainWidth = tw,
                        terrainHeight = th,
                        terrainPayload = TerrainPatchCodec.EncodeHeights(heights, tx, tz, tw, th),
                    };

                    if (_session.SendTerrain(message))
                    {
                        Sent++;
                    }
                }
            }
        }

        void EmitSplat(string guid, int resolution, float[,,] splat, int layers, int x, int z, int width, int height)
        {
            var tile = TerrainPatchCodec.TileForLayers(layers);
            for (var tz = z; tz < z + height; tz += tile)
            {
                for (var tx = x; tx < x + width; tx += tile)
                {
                    var tw = Mathf.Min(tile, x + width - tx);
                    var th = Mathf.Min(tile, z + height - tz);
                    var message = new WireMessage
                    {
                        type = MessageTypes.Terrain,
                        terrainValid = true,
                        terrainKind = TerrainPatchCodec.KindSplat,
                        terrainGuid = guid,
                        terrainResolution = resolution,
                        terrainLayers = layers,
                        terrainX = tx,
                        terrainZ = tz,
                        terrainWidth = tw,
                        terrainHeight = th,
                        terrainPayload = TerrainPatchCodec.EncodeSplat(splat, tx, tz, tw, th, layers),
                    };

                    if (_session.SendTerrain(message))
                    {
                        Sent++;
                    }
                }
            }
        }

        void OnTerrainReceived(WireMessage message)
        {
            if (message == null || !message.terrainValid || string.IsNullOrEmpty(message.terrainGuid))
            {
                return;
            }

            var data = FindTerrainData(message.terrainGuid);
            if (data == null)
            {
                Skipped++;
                LastNote = "terrain patch for an asset this editor does not have";
                return;
            }

            if (message.terrainKind == TerrainPatchCodec.KindHeight)
            {
                if (message.terrainResolution != data.heightmapResolution)
                {
                    Skipped++;
                    LastNote = "terrain resolution differs (" + data.heightmapResolution + " vs "
                        + message.terrainResolution + ")";
                    return;
                }

                float[,] region;
                if (!TerrainPatchCodec.TryDecodeHeights(message.terrainPayload, message.terrainWidth, message.terrainHeight, out region))
                {
                    Skipped++;
                    LastNote = "malformed terrain height tile";
                    return;
                }

                data.SetHeights(message.terrainX, message.terrainZ, region);
            }
            else if (message.terrainKind == TerrainPatchCodec.KindSplat)
            {
                if (message.terrainResolution != data.alphamapResolution
                    || message.terrainLayers != data.alphamapLayers)
                {
                    Skipped++;
                    LastNote = "terrain alphamap layout differs";
                    return;
                }

                float[,,] region;
                if (!TerrainPatchCodec.TryDecodeSplat(message.terrainPayload, message.terrainWidth, message.terrainHeight,
                        message.terrainLayers, out region))
                {
                    Skipped++;
                    LastNote = "malformed terrain splat tile";
                    return;
                }

                data.SetAlphamaps(message.terrainX, message.terrainZ, region);
            }
            else
            {
                Skipped++;
                LastNote = "unknown terrain patch kind";
                return;
            }

            EditorUtility.SetDirty(data);

            // The baseline is stale after a remote write; dropping it re-captures on the next poll and
            // guarantees the guest never echoes the host's own patch back.
            _baselines.Remove(message.terrainGuid);
            Applied++;
            LastNote = "applied terrain " + message.terrainKind + " tile";
        }

        TerrainData FindTerrainData(string guid)
        {
            TerrainData data;
            if (_byGuid.TryGetValue(guid, out data) && data != null)
            {
                return data;
            }

            RefreshTerrainMap();
            if (_byGuid.TryGetValue(guid, out data))
            {
                return data;
            }

            return null;
        }

        /// <summary>
        /// Rebuilds the GUID index from every loaded scene. Cheap enough at 0.6 s, and rebuilding it
        /// beats holding references to TerrainData assets that a scene close may have destroyed.
        /// </summary>
        void RefreshTerrainMap()
        {
            _byGuid.Clear();
            _terrains.Clear();

            var sceneCount = SceneManager.sceneCount;
            for (var i = 0; i < sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    continue;
                }

                var roots = scene.GetRootGameObjects();
                for (var r = 0; r < roots.Length; r++)
                {
                    var terrains = roots[r].GetComponentsInChildren<Terrain>(true);
                    for (var t = 0; t < terrains.Length; t++)
                    {
                        var data = terrains[t].terrainData;
                        if (data == null)
                        {
                            continue;
                        }

                        string guid;
                        long localId;
                        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data, out guid, out localId)
                            || string.IsNullOrEmpty(guid))
                        {
                            continue;
                        }

                        if (!_byGuid.ContainsKey(guid))
                        {
                            _byGuid.Add(guid, data);
                        }

                        if (!_terrains.Contains(data))
                        {
                            _terrains.Add(data);
                        }
                    }
                }
            }
        }
    }
}
