using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// v0.4.7: restoring an object faithfully — every component instance, every reference, sibling order.
    ///
    /// These are the cases where "it synced" and "it looks the same" part ways: a second AudioSource whose
    /// volume silently follows the first, a HingeJoint pointing at nothing, a hierarchy that arrives in name
    /// order. Each test pins one of those down without a server, because a failure here is a scene-maths
    /// failure and pointing at the network would be a lie.
    /// </summary>
    public sealed class RestoreTests
    {
        const string TempFolder = "Assets/__collab_tests";

        readonly List<string> _createdAssets = new List<string>();
        Scene _scene;
        int _saved;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "__collab_tests");
            }

            _scene = SceneManager.GetActiveScene();
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _createdAssets.Count; i++)
            {
                AssetDatabase.DeleteAsset(_createdAssets[i]);
            }

            _createdAssets.Clear();
        }

        GameObject SaveScene(string fileName, params GameObject[] objects)
        {
            _saved++;
            var path = TempFolder + "/" + fileName + "_" + _saved + ".unity";
            EditorSceneManager.SaveScene(_scene, path);
            _createdAssets.Add(path);
            return objects.Length > 0 ? objects[0] : null;
        }

        static GameObject Make(string name, params System.Type[] components)
        {
            var gameObject = new GameObject(name);
            SceneManager.MoveGameObjectToScene(gameObject, SceneManager.GetActiveScene());
            for (var i = 0; i < components.Length; i++)
            {
                gameObject.AddComponent(components[i]);
            }

            return gameObject;
        }

        static WireMessage SetProperty(string nodeId, string path, string oldValue, string newValue)
        {
            return new WireMessage
            {
                type = MessageTypes.Op,
                opKind = OpKinds.SetProperty,
                nodeId = nodeId,
                propertyPath = path,
                oldValue = oldValue,
                newValue = newValue,
            };
        }

        // ── component identity ────────────────────────────────────────────────

        [Test]
        public void ASecondComponentOfTheSameTypeKeepsItsOwnFields()
        {
            var host = Make("Two AudioSources", typeof(AudioSource), typeof(AudioSource));
            SaveScene("Slots", host);

            var paths = new List<string>();
            SceneComponentFields.CollectPaths(host, paths);

            var volumePaths = paths.Where(p => p.EndsWith("/m_Volume", System.StringComparison.Ordinal)).ToList();
            Assert.AreEqual(2, volumePaths.Count, "both AudioSources must expose their volume: "
                + string.Join(", ", paths));

            var first = SceneComponentFields.BuildPath(typeof(AudioSource).FullName, 0, "m_Volume");
            var second = SceneComponentFields.BuildPath(typeof(AudioSource).FullName, 1, "m_Volume");
            CollectionAssert.Contains(volumePaths, first);
            CollectionAssert.Contains(volumePaths, second);

            var sources = host.GetComponents<AudioSource>();
            sources[0].volume = 0.5f;
            sources[1].volume = 0.9f;

            Assert.IsTrue(ScenePropertyAccess.TryWrite(host, second, "0.25"), "slot 1 is not writable");
            Assert.AreEqual(0.25f, sources[1].volume, 0.001f, "slot 1 did not change");
            Assert.AreEqual(0.5f, sources[0].volume, 0.001f, "slot 0 changed instead of slot 1");
        }

        [Test]
        public void RemovingTheSecondComponentRemovesTheSameInstanceOnThePeer()
        {
            var host = Make("Remove Second", typeof(AudioSource), typeof(AudioSource));
            SaveScene("RemoveSlot", host);

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(host);
            var structure = new SceneStructure(registry);
            structure.Baseline(host, nodeId);

            // Two sources, then drop one: the op must name a concrete slot, not "an AudioSource".
            var target = host.GetComponents<AudioSource>()[1];
            Object.DestroyImmediate(target);

            var ops = structure.CollectComponentChanges(host, nodeId);
            Assert.AreEqual(1, ops.Count, "one removal expected");
            Assert.AreEqual(OpKinds.RemoveComponent, ops[0].Kind);
            Assert.AreEqual(1, ops[0].ComponentIndex, "the removed instance was the second one");
        }

        [Test]
        public void RemovingTheFirstOfTwoComponentsNamesTheFirstSlot()
        {
            var host = Make("Remove First", typeof(AudioSource), typeof(AudioSource));
            SaveScene("RemoveFirst", host);

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(host);
            var structure = new SceneStructure(registry);
            structure.Baseline(host, nodeId);

            var sources = host.GetComponents<AudioSource>();
            Object.DestroyImmediate(sources[0]);

            var ops = structure.CollectComponentChanges(host, nodeId);
            Assert.AreEqual(1, ops.Count, "one removal expected");
            Assert.AreEqual(0, ops[0].ComponentIndex,
                "the first instance was the one deleted; removing the last slot drops the other one");
        }

        // ── references ────────────────────────────────────────────────────────

        [Test]
        public void ASceneReferenceTravelsAsANodeIdAndComesBackAsTheSameObject()
        {
            var body = Make("Body", typeof(Rigidbody));
            var hinge = Make("Hinge", typeof(HingeJoint));
            SaveScene("Refs", body, hinge);

            var registry = new SceneNodeRegistry();
            var bodyId = registry.EnsureNodeId(body);
            var hingeId = registry.EnsureNodeId(hinge);

            var joint = hinge.GetComponent<HingeJoint>();
            joint.connectedBody = body.GetComponent<Rigidbody>();

            var path = SceneComponentFields.BuildPath(typeof(HingeJoint).FullName, 0, "m_ConnectedBody");
            string value;
            Assert.IsTrue(ScenePropertyAccess.TryRead(hinge, path, registry, out value), "reference not read");
            Assert.IsTrue(value.StartsWith(SceneReferenceCodec.NodePrefix, System.StringComparison.Ordinal),
                "a scene object must travel as a node id, got: " + value);
            StringAssert.Contains(bodyId, value);
            StringAssert.Contains(nameof(Rigidbody), value);

            joint.connectedBody = null;
            string problem;
            Assert.IsTrue(ScenePropertyAccess.TryWrite(hinge, path, registry, value, out problem), problem);
            Assert.AreEqual(body.GetComponent<Rigidbody>(), joint.connectedBody, "the reference did not resolve back");
        }

        [Test]
        public void AnUnresolvableReferenceLeavesAnErrorMarker()
        {
            var cube = Make("Missing Material", typeof(MeshFilter), typeof(MeshRenderer));
            SaveScene("MissingRef", cube);

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));

            var path = SceneComponentFields.BuildPath(typeof(MeshRenderer).FullName, 0, "m_Materials.Array.data[0]");
            var wire = SetProperty(nodeId, path, "", "ref:0000000000000000deadbeefdeadbeef:1234");

            string note;
            Assert.IsFalse(applier.Apply(wire, out note), "a reference to a missing asset must not be accepted");
            Assert.AreEqual(1, applier.MissingReferences);
            Assert.IsNotNull(note);

            var marker = cube.GetComponentInChildren<CollabMissingMarker>(true);
            Assert.IsNotNull(marker, "no ERROR stand-in was created");
            Assert.AreEqual(HideFlags.DontSave, marker.gameObject.hideFlags,
                "a placeholder must never be saved into the scene");
            Assert.AreEqual(path, marker.fieldPath);
        }

        [Test]
        public void AResolvedReferenceClearsItsErrorMarker()
        {
            var cube = Make("Clears", typeof(MeshFilter), typeof(MeshRenderer));
            SaveScene("Clears", cube);

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));
            var path = SceneComponentFields.BuildPath(typeof(MeshRenderer).FullName, 0, "m_Materials.Array.data[0]");

            string note;
            applier.Apply(SetProperty(nodeId, path, "", "ref:0000000000000000deadbeefdeadbeef:1"), out note);
            Assert.IsNotNull(cube.GetComponentInChildren<CollabMissingMarker>(true));

            Assert.IsTrue(applier.Apply(SetProperty(nodeId, path, "", SceneReferenceCodec.NullToken), out note), note);
            Assert.IsNull(cube.GetComponentInChildren<CollabMissingMarker>(true),
                "the ERROR marker outlived the reference that caused it");
        }

        // ── hierarchy order ───────────────────────────────────────────────────

        [Test]
        public void ARootObjectKeepsItsSiblingIndex()
        {
            var first = Make("First");
            var second = Make("Second");
            SaveScene("RootOrder", first, second);

            var registry = new SceneNodeRegistry();
            registry.EnsureNodeId(first);
            registry.EnsureNodeId(second);

            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));
            var payload = JsonUtility.ToJson(SceneStructure.Capture(second));
            var wire = new WireMessage
            {
                type = MessageTypes.Op,
                opKind = OpKinds.AddNode,
                nodeId = SceneStructure.NewNodeId(),
                parentNodeId = "",
                siblingIndex = 0,
                prefabGuid = "",
                propertyPath = "",
                oldValue = "",
                newValue = payload,
            };

            string note;
            Assert.IsTrue(applier.Apply(wire, out note), note);

            var created = registry.Entries()
                .Where(e => e.Key == wire.nodeId)
                .Select(e => e.Value)
                .FirstOrDefault();

            Assert.IsNotNull(created, "the added root is not registered");
            Assert.AreEqual(0, created.transform.GetSiblingIndex(), "the new root landed at the bottom of the list");
        }

        [Test]
        public void ReorderingTwoRootsIsAccepted()
        {
            var first = Make("Root A");
            var second = Make("Root B");
            SaveScene("RootReorder", first, second);

            var registry = new SceneNodeRegistry();
            var firstId = registry.EnsureNodeId(first);
            registry.EnsureNodeId(second);

            var structure = new SceneStructure(registry);
            structure.Baseline(first, firstId);

            var applier = new SceneOpApplier(registry, new SceneDiff(), structure);
            var wire = new WireMessage
            {
                type = MessageTypes.Op,
                opKind = OpKinds.ReorderChildren,
                nodeId = firstId,
                parentNodeId = "",
                siblingIndex = 1,
                propertyPath = "",
                oldValue = "",
                newValue = "",
            };

            string note;
            Assert.IsTrue(applier.Apply(wire, out note), "a root-level reorder was refused: " + note);
            Assert.AreEqual(1, first.transform.GetSiblingIndex());
        }

        // ── field completeness ────────────────────────────────────────────────

        [Test]
        public void EveryFieldOfAFreshlyAddedComponentTravels()
        {
            var host = Make("Fresh Component");
            SaveScene("Fresh", host);

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(host);

            var diff = new SceneDiff { Registry = registry };
            diff.Baseline(host, nodeId);

            var audio = host.AddComponent<AudioSource>();
            audio.volume = 0.37f;

            // The peer has just added the component with type defaults; the diff's baseline for the new
            // paths does not exist yet, so Collect alone would adopt them and send nothing.
            var all = diff.CollectAll(host, nodeId);
            var volume = SceneComponentFields.BuildPath(typeof(AudioSource).FullName, 0, "m_Volume");

            Assert.IsTrue(all.Any(o => o.PropertyPath == volume && o.NewValue == "0.37"),
                "the new component's fields were not sent: "
                + string.Join(", ", all.Select(o => o.PropertyPath + "=" + o.NewValue)));
        }
    }
}
