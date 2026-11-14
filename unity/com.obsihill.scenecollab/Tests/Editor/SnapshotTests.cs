using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// The v0.4.3 late-joiner path: a host turns its scene into ops, and an editor that has never seen
    /// those objects rebuilds them through the same applier that handles live edits.
    ///
    /// The test deliberately deletes the host objects between building the frames and applying them —
    /// otherwise "it works" would just mean the object was already there.
    /// </summary>
    public sealed class SnapshotTests
    {
        const string TempFolder = "Assets/__collab_snapshot_tests";

        readonly List<string> _createdAssets = new List<string>();
        Scene _scene;
        int _saved;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "__collab_snapshot_tests");
            }

            // Other fixtures intentionally leave prefab stand-ins behind after deleting test assets.
            // Snapshot completeness must be checked against this fixture's scene only.
            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            for (var i = 0; i < _createdAssets.Count; i++)
            {
                AssetDatabase.DeleteAsset(_createdAssets[i]);
            }

            _createdAssets.Clear();
        }

        void SaveScene(string fileName)
        {
            _saved++;
            var path = TempFolder + "/" + fileName + "_" + _saved + ".unity";
            EditorSceneManager.SaveScene(_scene, path);
            _createdAssets.Add(path);
        }

        static GameObject CreateCube(string name, Vector3 position, Transform parent)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            cube.transform.position = position;
            if (parent != null)
            {
                cube.transform.SetParent(parent, false);
            }

            return cube;
        }

        [Test]
        public void SnapshotRebuildsTheHostSceneOnAnEmptyEditor()
        {
            var root = new GameObject("Snapshot Root");
            var child = CreateCube("Snapshot Child", new Vector3(2f, 3f, 4f), root.transform);
            SaveScene("Snapshot");

            var hostRegistry = new SceneNodeRegistry();
            var childId = hostRegistry.EnsureNodeId(child);
            var rootId = hostRegistry.EnsureNodeId(root.transform.gameObject);
            Assert.IsTrue(!string.IsNullOrEmpty(childId), "the saved scene must give the child a node id");

            List<WireMessage> frames;
            string note;
            Assert.IsTrue(SceneSnapshot.TryBuild(_scene, hostRegistry, out frames, out note), note);
            Assert.GreaterOrEqual(frames.Count, 3, "start, at least one chunk, end");
            Assert.AreEqual(OpKinds.SnapshotStart, frames[0].opKind);
            Assert.AreEqual(OpKinds.SnapshotEnd, frames[frames.Count - 1].opKind);

            for (var i = 0; i < frames.Count; i++)
            {
                Assert.IsTrue(frames[i].snapshot, "every snapshot frame must be marked as such at index " + i);
            }

            // The receiving editor starts from nothing: no objects, no ids.
            Object.DestroyImmediate(root);

            var guestRegistry = new SceneNodeRegistry();
            var guestDiff = new SceneDiff();
            var guestStructure = new SceneStructure(guestRegistry);
            var applier = new SceneOpApplier(guestRegistry, guestDiff, guestStructure);
            var receiver = new SnapshotReceiver();

            HashSet<string> claimed = null;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                switch (frame.opKind)
                {
                    case OpKinds.SnapshotStart:
                        receiver.Begin();
                        break;

                    case OpKinds.SnapshotChunk:
                        Assert.IsTrue(receiver.ApplyChunk(frame.newValue, applier), receiver.LastError);
                        break;

                    case OpKinds.SnapshotEnd:
                        Assert.IsTrue(receiver.TryComplete(applier), receiver.LastError);
                        claimed = receiver.End();
                        break;
                }
            }

            Assert.IsNotNull(claimed, "the snapshot must end with snapshot_end");
            Assert.IsTrue(claimed.Contains(childId));

            GameObject rebuilt;
            Assert.IsTrue(guestRegistry.TryResolve(childId, out rebuilt), "the child id must resolve after the snapshot");
            Assert.IsNotNull(rebuilt);
            Assert.AreEqual("Snapshot Child", rebuilt.name);
            Assert.AreEqual(new Vector3(2f, 3f, 4f), rebuilt.transform.position);

            GameObject rebuiltRoot;
            Assert.IsTrue(guestRegistry.TryResolve(rootId, out rebuiltRoot));
            Assert.AreEqual(rebuiltRoot.transform, rebuilt.transform.parent, "the hierarchy must survive the snapshot");
        }

        [Test]
        public void SnapshotAddsEveryParentBeforeItsChildren()
        {
            // The receiver creates objects one op at a time, so an add that names a parent must arrive
            // after that parent exists. Depth sorting is the only thing enforcing it.
            var root = new GameObject("Snapshot Order Root");
            var middle = new GameObject("Snapshot Order Middle");
            middle.transform.SetParent(root.transform, false);
            CreateCube("Snapshot Order Leaf", Vector3.zero, middle.transform);
            SaveScene("SnapshotOrder");

            var hostRegistry = new SceneNodeRegistry();
            var ours = new HashSet<string>
            {
                hostRegistry.EnsureNodeId(root),
                hostRegistry.EnsureNodeId(middle),
                hostRegistry.EnsureNodeId(middle.transform.GetChild(0).gameObject),
            };

            // The editor's active scene is shared with the other tests, so only this test's objects count.
            Assert.IsFalse(ours.Contains(null));

            List<WireMessage> frames;
            string note;
            Assert.IsTrue(SceneSnapshot.TryBuild(_scene, hostRegistry, out frames, out note), note);

            var seen = new HashSet<string>();
            var adds = 0;
            for (var i = 0; i < frames.Count; i++)
            {
                if (frames[i].opKind != OpKinds.SnapshotChunk)
                {
                    continue;
                }

                var chunk = JsonUtility.FromJson<SnapshotChunk>(frames[i].newValue);
                for (var j = 0; j < chunk.ops.Length; j++)
                {
                    var op = chunk.ops[j];
                    if (op.opKind != OpKinds.AddNode || !ours.Contains(op.nodeId))
                    {
                        continue;
                    }

                    adds++;

                    if (!string.IsNullOrEmpty(op.parentNodeId))
                    {
                        Assert.IsTrue(seen.Contains(op.parentNodeId),
                            "parent " + op.parentNodeId + " must be added before " + op.nodeId);
                    }

                    seen.Add(op.nodeId);
                }
            }

            Assert.AreEqual(3, adds, "root, middle and leaf all travel as add_node");
        }

        [Test]
        public void AddNodeAtTheSceneRootDoesNotThrow()
        {
            // Regression: the sibling-index step read `created.transform.parent.childCount`, which threw
            // for every root-level object — so a peer's new cube arrived as an exception, not a cube.
            var registry = new SceneNodeRegistry();
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));

            var payload = JsonUtility.ToJson(new SceneOpApplier.AddNodePayload
            {
                name = "Root Add",
                active = true,
                rw = 1f,
                sx = 1f,
                sy = 1f,
                sz = 1f,
            });

            string note;
            Assert.IsTrue(applier.Apply(new WireMessage
            {
                opKind = OpKinds.AddNode,
                nodeId = "0123456789abcdef",
                parentNodeId = "",
                siblingIndex = 3,
                propertyPath = "",
                oldValue = "",
                newValue = payload,
            }, out note), note);

            GameObject created;
            Assert.IsTrue(registry.TryResolve("0123456789abcdef", out created));
            Assert.IsNull(created.transform.parent, "a root add must stay at the scene root");

            Object.DestroyImmediate(created);
        }

        [Test]
        public void SnapshotChunkSurvivesJsonRoundTrip()
        {
            var chunk = new SnapshotChunk
            {
                index = 2,
                total = 5,
                ops = new[]
                {
                    new WireMessage
                    {
                        snapshot = true,
                        opKind = OpKinds.SetProperty,
                        nodeId = "node-1",
                        propertyPath = "t/px",
                        oldValue = "",
                        newValue = "1.5",
                    },
                },
            };

            var json = JsonUtility.ToJson(chunk);
            var back = JsonUtility.FromJson<SnapshotChunk>(json);

            Assert.AreEqual(2, back.index);
            Assert.AreEqual(5, back.total);
            Assert.AreEqual(1, back.ops.Length);
            Assert.AreEqual(OpKinds.SetProperty, back.ops[0].opKind);
            Assert.AreEqual("t/px", back.ops[0].propertyPath);
            Assert.AreEqual("1.5", back.ops[0].newValue);
        }

        [Test]
        public void ReceiverRefusesAChunkWithNoStart()
        {
            var registry = new SceneNodeRegistry();
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));
            var receiver = new SnapshotReceiver();

            Assert.IsFalse(receiver.ApplyChunk("{}", applier));
            Assert.IsNotNull(receiver.LastError);
            Assert.IsFalse(receiver.Active);
        }

        [Test]
        public void ReceiverCannotFinishWithMissingOrDuplicateChunks()
        {
            var registry = new SceneNodeRegistry();
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));
            var receiver = new SnapshotReceiver();
            receiver.Begin();
            var first = JsonUtility.ToJson(new SnapshotChunk { index = 0, total = 2, ops = new WireMessage[0] });
            Assert.IsTrue(receiver.ApplyChunk(first, applier));
            Assert.IsFalse(receiver.TryComplete(applier));
            Assert.IsFalse(receiver.ApplyChunk(first, applier), "a repeated frame cannot count twice");
            Assert.IsFalse(receiver.ApplyChunk(JsonUtility.ToJson(new SnapshotChunk
                { index = 1, total = 3, ops = new WireMessage[0] }), applier), "total cannot change mid-transfer");
            Assert.IsTrue(receiver.ApplyChunk(JsonUtility.ToJson(new SnapshotChunk
                { index = 1, total = 2, ops = new WireMessage[0] }), applier));
            Assert.IsTrue(receiver.TryComplete(applier));
        }

        [Test]
        public void ReceiverFailsCompletionWhenAFieldStillCannotResolve()
        {
            var registry = new SceneNodeRegistry();
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));
            var receiver = new SnapshotReceiver();
            receiver.Begin();
            Assert.IsTrue(receiver.ApplyChunk(JsonUtility.ToJson(new SnapshotChunk
            {
                index = 0, total = 1, ops = new[] { new WireMessage { opKind = OpKinds.SetProperty,
                    nodeId = "missing", propertyPath = ScenePropertyAccess.LocalPosition, newValue = "1,2,3" } }
            }), applier));
            Assert.IsFalse(receiver.TryComplete(applier));
            Assert.IsFalse(receiver.IsComplete);
            StringAssert.Contains("could not be applied", receiver.LastError);
        }
    }
}
