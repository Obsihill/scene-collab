using NUnit.Framework;
using SceneCollab.Editor.SceneSync;
using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// v0.5: the terrain wire format.
    ///
    /// Terrain is the one thing this tool cannot learn from the scene graph: heights and splat weights
    /// live in a TerrainData asset, so they need their own diff, their own quantisation and their own
    /// size budget. These tests pin those three down arithmetically — no server, no second editor.
    /// What they cannot prove is that a sculpted ridge looks identical on both machines; that needs
    /// two editors and belongs in the live harness.
    /// </summary>
    public sealed class TerrainPatchTests
    {
        static float[,] Flat(int resolution, float value)
        {
            var heights = new float[resolution, resolution];
            for (var z = 0; z < resolution; z++)
            {
                for (var x = 0; x < resolution; x++)
                {
                    heights[z, x] = value;
                }
            }

            return heights;
        }

        [Test]
        public void ADiffReportsExactlyTheSculptedRectangle()
        {
            var before = Flat(64, 0.5f);
            var after = Flat(64, 0.5f);

            for (var z = 20; z < 30; z++)
            {
                for (var x = 10; x < 18; x++)
                {
                    after[z, x] = 0.75f;
                }
            }

            int x0, z0, width, height;
            Assert.IsTrue(TerrainPatchCodec.TryDiffHeights(before, after, out x0, out z0, out width, out height));
            Assert.AreEqual(10, x0, "left edge");
            Assert.AreEqual(20, z0, "bottom edge");
            Assert.AreEqual(8, width, "width");
            Assert.AreEqual(10, height, "height");
        }

        [Test]
        public void AnUnchangedHeightmapProducesNoPatch()
        {
            var before = Flat(32, 0.25f);
            var after = Flat(32, 0.25f);

            int x0, z0, width, height;
            Assert.IsFalse(TerrainPatchCodec.TryDiffHeights(before, after, out x0, out z0, out width, out height),
                "a dirty asset whose heights did not move (a splat paint) must not send a height tile");
        }

        [Test]
        public void HeightsSurviveTheRoundTripWithinQuantisationError()
        {
            // Indexed [z, x] — Unity's heightmap order, which is also the order the codec uses.
            var heights = Flat(16, 0f);
            heights[7, 4] = 0.123456f;
            heights[8, 6] = 1f;
            heights[15, 15] = 0.0001f;

            var payload = TerrainPatchCodec.EncodeHeights(heights, 4, 7, 3, 2);
            float[,] region;
            Assert.IsTrue(TerrainPatchCodec.TryDecodeHeights(payload, 3, 2, out region));

            Assert.AreEqual(3, region.GetLength(1), "width");
            Assert.AreEqual(2, region.GetLength(0), "height");
            Assert.AreEqual(0.123456f, region[0, 0], 1f / 65535f, "sample at the patch origin");
            Assert.AreEqual(1f, region[1, 2], 1f / 65535f, "sample at the far corner");
        }

        [Test]
        public void ADecodeRejectsAPayloadThatDoesNotMatchItsRectangle()
        {
            var payload = TerrainPatchCodec.EncodeHeights(Flat(8, 0.5f), 0, 0, 4, 4);
            float[,] region;
            Assert.IsFalse(TerrainPatchCodec.TryDecodeHeights(payload, 4, 5, out region),
                "a 4x4 payload read as 4x5 is a corrupted terrain, not a warning");
        }

        [Test]
        public void ADecodeRejectsGarbage()
        {
            float[,] region;
            Assert.IsFalse(TerrainPatchCodec.TryDecodeHeights("not base64 %%%", 2, 2, out region));
            Assert.IsFalse(TerrainPatchCodec.TryDecodeHeights(string.Empty, 2, 2, out region));
        }

        static float[,,] FlatSplat(int resolution, int layers, float value)
        {
            var splat = new float[resolution, resolution, layers];
            for (var x = 0; x < resolution; x++)
            {
                for (var z = 0; z < resolution; z++)
                {
                    for (var l = 0; l < layers; l++)
                    {
                        splat[x, z, l] = value;
                    }
                }
            }

            return splat;
        }

        [Test]
        public void ASplatPaintRoundTripsAndDiffsToOneRectangle()
        {
            var before = FlatSplat(32, 2, 0f);
            before[0, 0, 0] = 1f;
            var after = (float[,,])before.Clone();
            after[5, 6, 1] = 0.5f;

            int x0, z0, width, height;
            Assert.IsTrue(TerrainPatchCodec.TryDiffSplat(before, after, out x0, out z0, out width, out height));
            Assert.AreEqual(5, x0);
            Assert.AreEqual(6, z0);
            Assert.AreEqual(1, width);
            Assert.AreEqual(1, height);

            var payload = TerrainPatchCodec.EncodeSplat(after, 5, 6, 1, 1, 2);
            float[,,] region;
            Assert.IsTrue(TerrainPatchCodec.TryDecodeSplat(payload, 1, 1, 2, out region));
            Assert.AreEqual(0.5f, region[0, 0, 1], 1f / 255f);
        }

        [Test]
        public void ATerrainWithMoreLayersGetsSmallerTilesSoOneFrameStaysSmall()
        {
            var fourLayers = TerrainPatchCodec.TileForLayers(4);
            var sixtyFourLayers = TerrainPatchCodec.TileForLayers(64);

            Assert.LessOrEqual(fourLayers * fourLayers * 4, TerrainPatchCodec.MaxPayloadBytes);
            Assert.LessOrEqual(sixtyFourLayers * sixtyFourLayers * 64, TerrainPatchCodec.MaxPayloadBytes);
            Assert.Less(sixtyFourLayers, fourLayers, "more layers must mean a smaller tile");
        }

        [Test]
        public void AHeightTileFitsOneMessage()
        {
            var bytes = TerrainPatchCodec.HeightTile * TerrainPatchCodec.HeightTile * 2;
            Assert.LessOrEqual(bytes, TerrainPatchCodec.MaxPayloadBytes,
                "a height tile is the largest single payload the codec may produce");
        }
    }
}
