using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// v0.5: the terrain path end to end, on a real TerrainData asset.
    ///
    /// <see cref="TerrainPatchTests"/> covers the wire format. This covers what the format cannot: that
    /// a sculpt is noticed at all (the scene diff sees nothing, because a terrain is a pointer to an
    /// asset), that the tile lands at the right offset on the other side, and that a terrain which does
    /// not match is refused instead of corrupted.
    /// </summary>
    public sealed class TerrainSyncTests
    {
        const string Folder = "Assets/__SceneCollabTerrainTest";
        const int Resolution = 129;

        GameObject _host;
        TerrainData _data;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "__SceneCollabTerrainTest");
            }

            _data = new TerrainData();
            _data.heightmapResolution = Resolution;
            _data.size = new Vector3(512f, 60f, 512f);

            _host = new GameObject("SceneCollab Terrain");
            var terrain = _host.AddComponent<Terrain>();
            terrain.terrainData = _data;
            AssetDatabase.CreateAsset(_data, Folder + "/Terrain.asset");
        }

        [TearDown]
        public void TearDown()
        {
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
            }

            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void AHostSculptTravelsAsOneTileAtTheRightOffset()
        {
            var session = new FakeSession { IsHost = true };
            var sync = new TerrainSync();
            try
            {
                sync.Bind(session);

                // The first poll only takes the baseline: nothing has been sculpted yet.
                sync.Tick();
                Assert.AreEqual(0, session.SentTerrain.Count, "a fresh terrain must not send its whole heightmap");

                var blob = new float[8, 8];
                for (var z = 0; z < 8; z++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        blob[z, x] = 0.05f + 0.1f * (z * 8 + x) / 64f;
                    }
                }

                _data.SetHeights(20, 30, blob);
                EditorUtility.SetDirty(_data);
                TickAgain(sync);

                Assert.AreEqual(1, session.SentTerrain.Count, "one stroke must arrive as one tile");

                var tile = session.SentTerrain[0];
                Assert.AreEqual("height", tile.terrainKind);
                Assert.AreEqual(20, tile.terrainX, "tile x");
                Assert.AreEqual(30, tile.terrainZ, "tile z");
                Assert.AreEqual(8, tile.terrainWidth, "tile width");
                Assert.AreEqual(8, tile.terrainHeight, "tile height");
                Assert.AreEqual(_data.heightmapResolution, tile.terrainResolution, "tile resolution");

                float[,] region;
                Assert.IsTrue(TerrainPatchCodec.TryDecodeHeights(tile.terrainPayload, 8, 8, out region));
                Assert.AreEqual(0.05f, region[0, 0], 1f / 65535f, "first sample");
                Assert.AreEqual(0.05f + 0.1f * 63f / 64f, region[7, 7], 1f / 65535f, "last sample");

                Assert.AreEqual(1, sync.Sent, "the counter the window shows");
            }
            finally
            {
                sync.Unbind();
            }
        }

        [Test]
        public void AGuestAppliesThePatchToItsOwnTerrainAndMarksItDirty()
        {
            var session = new FakeSession { IsHost = false };
            var sync = new TerrainSync();
            try
            {
                sync.Bind(session);

                var payload = TerrainPatchCodec.EncodeHeights(Blob(4, 4, 0.5f), 0, 0, 4, 4);
                session.Deliver(new WireMessage
                {
                    type = MessageTypes.Terrain,
                    terrainValid = true,
                    terrainKind = "height",
                    terrainGuid = GuidOf(_data),
                    terrainResolution = _data.heightmapResolution,
                    terrainX = 10,
                    terrainZ = 12,
                    terrainWidth = 4,
                    terrainHeight = 4,
                    terrainPayload = payload,
                });

                Assert.AreEqual(1, sync.Applied, "the patch must be applied");
                Assert.AreEqual(0, sync.Skipped, "and must not be counted as skipped");

                var heights = _data.GetHeights(10, 12, 4, 4);
                Assert.AreEqual(0.5f, heights[0, 0], 1f / 65535f, "the tile origin was written");
                Assert.AreEqual(0.5f, heights[3, 3], 1f / 65535f, "the far corner was written");
                Assert.IsTrue(EditorUtility.IsDirty(_data), "the guest's asset must be marked dirty for the host to save");
            }
            finally
            {
                sync.Unbind();
            }
        }

        [Test]
        public void APatchForAnotherResolutionIsRefusedNotApplied()
        {
            var session = new FakeSession { IsHost = false };
            var sync = new TerrainSync();
            try
            {
                sync.Bind(session);

                session.Deliver(new WireMessage
                {
                    type = MessageTypes.Terrain,
                    terrainValid = true,
                    terrainKind = "height",
                    terrainGuid = GuidOf(_data),
                    terrainResolution = Resolution * 2,
                    terrainX = 0,
                    terrainZ = 0,
                    terrainWidth = 4,
                    terrainHeight = 4,
                    terrainPayload = TerrainPatchCodec.EncodeHeights(Blob(4, 4, 0.5f), 0, 0, 4, 4),
                });

                Assert.AreEqual(0, sync.Applied, "a mismatched terrain must not be written");
                Assert.AreEqual(1, sync.Skipped, "it must be reported instead");
                Assert.AreEqual(0f, _data.GetHeights(0, 0, 1, 1)[0, 0], 1e-6f, "and the terrain must be untouched");
            }
            finally
            {
                sync.Unbind();
            }
        }

        /// <summary>
        /// <see cref="TerrainSync.Tick"/> polls at 0.6 s intervals, so a second edit inside one interval
        /// would be invisible — which is the point: sculpt strokes are far coarser than that.
        /// </summary>
        static void TickAgain(TerrainSync sync)
        {
            Thread.Sleep((int)(TerrainSync.PollSeconds * 1000) + 200);
            sync.Tick();
        }

        static float[,] Blob(int width, int height, float value)
        {
            var heights = new float[height, width];
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    heights[z, x] = value;
                }
            }

            return heights;
        }

        static string GuidOf(UnityEngine.Object asset)
        {
            string guid;
            long localId;
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out localId));
            return guid;
        }

        /// <summary>
        /// The four members <see cref="TerrainSync"/> touches, and nothing else. A real session needs a
        /// server; the point of these tests is that the terrain path does not.
        /// </summary>
        sealed class FakeSession : ISceneCollabSession
        {
            public bool IsHost { get; set; }

            public readonly List<WireMessage> SentTerrain = new List<WireMessage>();

            public void Deliver(WireMessage patch)
            {
                var handler = TerrainReceived;
                if (handler != null)
                {
                    handler(patch);
                }
            }

            public bool SendTerrain(WireMessage patch)
            {
                SentTerrain.Add(patch);
                return true;
            }

            public event Action<WireMessage> TerrainReceived;

            public CollabConnectionState State
            {
                get { return CollabConnectionState.Connected; }
            }

            public string ClientId
            {
                get { return "fake"; }
            }

            public bool IsScribe
            {
                get { return false; }
            }

            public string SceneGuid
            {
                get { return string.Empty; }
            }

            public string ScenePath
            {
                get { return string.Empty; }
            }

            public string SceneName
            {
                get { return string.Empty; }
            }

            public string[] LockedNodeIds
            {
                get { return Array.Empty<string>(); }
            }

            public string[] AssetGuids
            {
                get { return Array.Empty<string>(); }
            }

            public int AssetCount
            {
                get { return 0; }
            }

            public bool IsLockedByOther(string nodeId)
            {
                return false;
            }

            public long Revision
            {
                get { return 0; }
            }

            public string RoomName
            {
                get { return string.Empty; }
            }

            public IReadOnlyList<ParticipantInfo> Participants
            {
                get { return Array.Empty<ParticipantInfo>(); }
            }

            public event Action StateChanged;

            public event Action ParticipantsChanged;

            public event Action<WireMessage> OperationReceived;

            public event Action<WireMessage> OperationAcknowledged;

            public event Action<string, string> ErrorReceived;

            public event Action LocksChanged;

            public event Action AssetManifestChanged;

            public event Action<WireMessage> ViewReceived;

            public void Connect(CollabSettings settings)
            {
                throw new NotSupportedException();
            }

            public void Disconnect()
            {
                throw new NotSupportedException();
            }

            public bool SendOperation(SceneCollab.Domain.SceneOperation operation)
            {
                throw new NotSupportedException();
            }

            public bool SendLock(string nodeId, bool release)
            {
                throw new NotSupportedException();
            }

            public bool SendSnapshotFrame(WireMessage frame)
            {
                throw new NotSupportedException();
            }

            public bool SendView(float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW,
                float fov)
            {
                throw new NotSupportedException();
            }

            public bool PublishAssetCount(int count)
            {
                throw new NotSupportedException();
            }

            public bool PublishAssets(string[] assetGuids)
            {
                throw new NotSupportedException();
            }
        }
    }
}
