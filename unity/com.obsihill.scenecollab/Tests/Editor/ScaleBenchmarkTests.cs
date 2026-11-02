using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using SceneCollab.Domain;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// Measures what a big scene costs, instead of guessing.
    ///
    /// The audit's third and fourth points are about cost, and cost is not something to argue about:
    /// a sweep is either fast enough on a real hierarchy or it is not. These tests build a scene big
    /// enough to hurt, run the real diff over it, and log the numbers under <c>[BENCH]</c> so the
    /// release notes can quote measurements instead of estimations.
    ///
    /// The ceilings are deliberately loose. They exist to catch an order-of-magnitude regression on a
    /// loaded machine, not to police milliseconds, because a CI box is not this box.
    /// </summary>
    public sealed class ScaleBenchmarkTests
    {
        const int Objects = 1500;
        const int TerrainResolution = 1025;

        readonly List<GameObject> _objects = new List<GameObject>();
        readonly List<string> _ids = new List<string>();
        GameObject _root;
        Scene _scene;
        string _scenePath;

        [SetUp]
        public void SetUp()
        {
            // The scene has to be a saved asset inside the project: a node id is a GlobalObjectId, and an
            // object in a scene that is not an asset under Assets/ has none. A test that saved elsewhere
            // would measure nothing at all and still pass its timings.
            if (!AssetDatabase.IsValidFolder("Assets/__collab_scale"))
            {
                AssetDatabase.CreateFolder("Assets", "__collab_scale");
            }

            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _scenePath = "Assets/__collab_scale/Scale_" + System.Guid.NewGuid().ToString("N") + ".unity";
            EditorSceneManager.SaveScene(_scene, _scenePath);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
            {
                Object.DestroyImmediate(_root);
            }

            _objects.Clear();
            _ids.Clear();

            if (!string.IsNullOrEmpty(_scenePath))
            {
                AssetDatabase.DeleteAsset(_scenePath);
            }
        }

        void BuildScene(SceneNodeRegistry registry)
        {
            _root = new GameObject("__SceneCollabScaleRoot");

            for (var i = 0; i < Objects; i++)
            {
                var go = new GameObject("Node " + i);
                go.transform.SetParent(_root.transform, false);
                go.transform.localPosition = new Vector3(i * 0.1f, 0f, i * 0.2f);

                if (i % 4 == 0)
                {
                    go.AddComponent<BoxCollider>();
                }

                _objects.Add(go);
            }

            // The scene must be saved with the objects inside it before ids exist: a node id is a
            // GlobalObjectId, and an object that was never committed to the scene asset has none.
            EditorSceneManager.SaveScene(_scene, _scenePath);

            for (var i = 0; i < _objects.Count; i++)
            {
                _ids.Add(registry.EnsureNodeId(_objects[i]));
            }
        }

        [Test]
        public void SweepingThousandsOfObjectsStaysLinearAndQuiet()
        {
            var registry = new SceneNodeRegistry();
            BuildScene(registry);
            Assert.IsNotEmpty(_ids[0], "the scene must give every object a node id, or nothing is measured");

            var diff = new SceneDiff { Registry = registry };

            var watch = Stopwatch.StartNew();
            for (var i = 0; i < _objects.Count; i++)
            {
                diff.Baseline(_objects[i], _ids[i]);
            }

            var primeMs = watch.Elapsed.TotalMilliseconds;

            var operations = new List<SceneOperation>();

            watch.Restart();
            var idleOps = 0;
            for (var i = 0; i < _objects.Count; i++)
            {
                diff.CollectInto(_objects[i], _ids[i], operations);
                idleOps += operations.Count;
            }

            var idleMs = watch.Elapsed.TotalMilliseconds;

            // A tenth of the scene moves, which is what a marquee transform or a multi-select does.
            for (var i = 0; i < _objects.Count; i += 10)
            {
                _objects[i].transform.localPosition += new Vector3(1f, 0f, 0f);
            }

            watch.Restart();
            var changedOps = 0;
            for (var i = 0; i < _objects.Count; i++)
            {
                diff.CollectInto(_objects[i], _ids[i], operations);
                changedOps += operations.Count;
            }

            var changedMs = watch.Elapsed.TotalMilliseconds;

            var idlePerObjectUs = idleMs * 1000.0 / _objects.Count;
            var changedPerObjectUs = changedMs * 1000.0 / _objects.Count;

            UnityEngine.Debug.Log(string.Format(
                "[BENCH] scene-diff objects={0} baseline={1:F0}ms idle-sweep={2:F0}ms ({3:F1}us/obj, {4} ops) "
                + "changed-10pct-sweep={5:F0}ms ({6:F1}us/obj, {7} ops)",
                _objects.Count, primeMs, idleMs, idlePerObjectUs, idleOps, changedMs, changedPerObjectUs, changedOps));

            Assert.AreEqual(0, idleOps, "an idle sweep must produce no ops at all");
            Assert.AreEqual(_objects.Count / 10, changedOps, "one transform x per touched object, nothing else");
            Assert.Less(idleMs, 5000.0, "idle sweep of " + Objects + " objects should not take seconds");
            Assert.Less(changedMs, 5000.0, "changed sweep should not take seconds");
        }

        [Test]
        public void CollectingEveryFieldOfOneObjectStaysSubMillisecond()
        {
            var registry = new SceneNodeRegistry();
            BuildScene(registry);

            var diff = new SceneDiff { Registry = registry };
            var go = _objects[0];
            var id = _ids[0];
            diff.Baseline(go, id);

            var operations = new List<SceneOperation>();
            const int Rounds = 200;

            var watch = Stopwatch.StartNew();
            for (var i = 1; i <= Rounds; i++)
            {
                go.transform.localPosition = new Vector3(i, 0f, 0f);
                diff.CollectInto(go, id, operations);
                Assert.AreEqual(1, operations.Count, "one changed field, one op");
            }

            var perCallUs = watch.Elapsed.TotalMilliseconds * 1000.0 / Rounds;
            UnityEngine.Debug.Log(string.Format(
                "[BENCH] per-object collect rounds={0} {1:F1}us/call", Rounds, perCallUs));

            Assert.Less(perCallUs, 1000.0, "a single object's diff should be well under a millisecond");
        }

        [Test]
        public void StructureWalkOnThousandsOfObjects()
        {
            var registry = new SceneNodeRegistry();
            BuildScene(registry);

            var structure = new SceneStructure(registry);
            for (var i = 0; i < _objects.Count; i++)
            {
                structure.Baseline(_objects[i], _ids[i]);
            }

            var watch = Stopwatch.StartNew();
            var idleNew = structure.DetectUnregistered(_scene);
            var idleNewMs = watch.Elapsed.TotalMilliseconds;

            watch.Restart();
            var idleRemoved = structure.DetectRemoved();
            var idleRemovedMs = watch.Elapsed.TotalMilliseconds;

            // Adding objects is the structural edit that walks the hierarchy; it must find only the new ones.
            for (var i = 0; i < 10; i++)
            {
                var go = new GameObject("Late " + i);
                go.transform.SetParent(_root.transform, false);
            }

            EditorSceneManager.SaveScene(_scene, _scenePath);

            watch.Restart();
            var afterNew = structure.DetectUnregistered(_scene);
            var afterNewMs = watch.Elapsed.TotalMilliseconds;

            UnityEngine.Debug.Log(string.Format(
                "[BENCH] structure objects={0} idle-scan={1:F1}ms ({2} new) removed-check={3:F1}ms "
                + "after-10-adds={4:F1}ms ({5} new)",
                _objects.Count, idleNewMs, idleNew.Count, idleRemovedMs, afterNewMs, afterNew.Count));

            Assert.AreEqual(0, idleNew.Count, "a registered scene has nothing new");
            Assert.AreEqual(0, idleRemoved.Count, "nothing was deleted");
            Assert.AreEqual(10, afterNew.Count, "ten new objects, ten additions");
            Assert.Less(idleNewMs, 5000.0);
            Assert.Less(afterNewMs, 5000.0);
        }

        [Test]
        public void TerrainScanReadsOneWireTileAtATime()
        {
            var data = new TerrainData { heightmapResolution = TerrainResolution };
            var sync = new TerrainSync();

            int x, z, width, height;

            // The first call seeds the baseline (one full read, as a real first poll does) and scans.
            var watch = Stopwatch.StartNew();
            var unchanged = sync.ScanForTest(data, out x, out z, out width, out height);
            var idleMs = watch.Elapsed.TotalMilliseconds;
            var tilesAfterIdle = sync.TilesRead;

            // One sculpt spot of four samples, well inside the grid.
            var patch = new float[4, 4];
            for (var r = 0; r < 4; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    patch[r, c] = 0.25f;
                }
            }

            data.SetHeights(300, 340, patch);

            watch.Restart();
            var changed = sync.ScanForTest(data, out x, out z, out width, out height);
            var changedMs = watch.Elapsed.TotalMilliseconds;

            UnityEngine.Debug.Log(string.Format(
                "[BENCH] terrain {0}x{0} idle-scan={1:F0}ms wire-tiles={2} changed-scan={3:F0}ms "
                + "patch=x{4} z{5} w{6} h{7}",
                TerrainResolution - 1, idleMs, tilesAfterIdle, changedMs, x, z, width, height));

            Assert.IsFalse(unchanged, "an untouched terrain must report no patch");
            Assert.AreEqual(81, tilesAfterIdle, "1025 samples at 128 per tile is 9x9 reads");

            Assert.IsTrue(changed, "a sculpt must be noticed");
            Assert.AreEqual(300, x, "the patch starts where the stroke did");
            Assert.AreEqual(340, z, "the patch starts where the stroke did");
            Assert.AreEqual(4, width, "and stays as small as the stroke");
            Assert.AreEqual(4, height, "and stays as small as the stroke");
            Assert.Less(changedMs, 5000.0);

            Object.DestroyImmediate(data);
        }
    }
}
