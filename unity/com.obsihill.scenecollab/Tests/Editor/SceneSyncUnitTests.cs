using System.Collections.Generic;
using System.IO;
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
    /// Unit-level checks for the v0.2 sync layer. Everything here runs without a server: the point is
    /// that capture, diff, apply and the echo guard are provably correct on their own, so a failing
    /// live session means the network, not the scene maths.
    ///
    /// These tests save real scenes under <c>Assets/__collab_tests/</c> because a node id only exists
    /// once Unity has written the object to disk — that is a property of the design, not of the test.
    /// </summary>
    public sealed class SceneSyncUnitTests
    {
        const string TempFolder = "Assets/__collab_tests";

        readonly List<string> _createdAssets = new List<string>();
        Scene _scene;
        int _savedScenes;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "__collab_tests");
            }

            // Work inside whatever scene the editor already has open. Creating a second scene is not an
            // option: a batch-mode editor starts on an untitled scene, and Unity refuses to add another
            // one next to an unsaved scene.
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

        GameObject SaveSceneWithCube(string fileName, out string scenePath)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Synced Cube";
            SceneManager.MoveGameObjectToScene(cube, _scene);

            // Unique per save: each test gets its own scene asset (and therefore its own scene GUID),
            // which is what a node id is built from.
            _savedScenes++;
            scenePath = TempFolder + "/" + fileName + "_" + _savedScenes + ".unity";
            EditorSceneManager.SaveScene(_scene, scenePath);
            _createdAssets.Add(scenePath);
            return cube;
        }

        // ── codec ─────────────────────────────────────────────────────────────

        [Test]
        public void Vector3RoundTripsThroughInvariantText()
        {
            var cube = SaveSceneWithCube("Codec", out _);
            Assert.IsTrue(ScenePropertyAccess.TryWrite(cube, ScenePropertyAccess.LocalPosition, "1.5,-2.25,3"));

            string value;
            Assert.IsTrue(ScenePropertyAccess.TryRead(cube, ScenePropertyAccess.LocalPosition, out value));
            Assert.AreEqual("1.5,-2.25,3", value);
        }

        [Test]
        public void FloatTextNeverCarriesCultureSeparators()
        {
            var cube = SaveSceneWithCube("Culture", out _);
            Assert.IsTrue(ScenePropertyAccess.TryWrite(cube, ScenePropertyAccess.LocalPosition, "0.5,0,0"));

            string value;
            ScenePropertyAccess.TryRead(cube, ScenePropertyAccess.LocalPosition, out value);
            Assert.IsFalse(value.Contains("0,5"), "value used a comma decimal separator: " + value);
            Assert.AreEqual("0.5,0,0", value);
        }

        [Test]
        public void UnparseableValueIsRefusedAndLeavesTheObjectUntouched()
        {
            var cube = SaveSceneWithCube("Refuse", out _);
            cube.transform.localPosition = new Vector3(9f, 9f, 9f);
            Assert.IsFalse(ScenePropertyAccess.TryWrite(cube, ScenePropertyAccess.LocalPosition, "not a vector"));
            Assert.AreEqual(new Vector3(9f, 9f, 9f), cube.transform.localPosition);
        }

        [Test]
        public void UntrackedPathIsRefused()
        {
            Assert.IsFalse(ScenePropertyAccess.IsTracked("m_Foo"));
            Assert.IsFalse(ScenePropertyAccess.IsTracked(null));
        }

        // ── registry ──────────────────────────────────────────────────────────

        [Test]
        public void NodeIdIsStableAcrossRegistryInstancesForASavedObject()
        {
            var cube = SaveSceneWithCube("Stable", out var scenePath);

            var first = new SceneNodeRegistry();
            var second = new SceneNodeRegistry();
            var idA = first.EnsureNodeId(cube);
            var idB = second.EnsureNodeId(cube);

            Assert.IsNotNull(idA);
            Assert.AreEqual(idA, idB, "two editors opening the same scene must agree on the id");
            Assert.IsTrue(StableNodeId.IsValid(idA));

            var sidecar = scenePath + SceneNodeRegistry.SidecarSuffix;
            first.Save(sidecar, _scene);
            _createdAssets.Add(sidecar);
            Assert.IsTrue(File.Exists(sidecar), "sidecar was not written: " + sidecar);
        }

        [Test]
        public void DestroyedObjectResolvesToNothingRatherThanAWrongObject()
        {
            var cube = SaveSceneWithCube("Destroyed", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            Assert.IsNotNull(nodeId);

            Object.DestroyImmediate(cube);

            GameObject resolved;
            Assert.IsFalse(registry.TryResolve(nodeId, out resolved));
            Assert.IsNull(resolved);
        }

        // ── diff + apply ──────────────────────────────────────────────────────

        [Test]
        public void MovingAnObjectProducesOneOpWithBothValues()
        {
            var cube = SaveSceneWithCube("Diff", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var diff = new SceneDiff();

            diff.Baseline(cube, nodeId);
            Assert.AreEqual(0, diff.Collect(cube, nodeId).Count, "baseline must not emit anything");

            cube.transform.localPosition = new Vector3(1f, 2f, 3f);

            var operations = diff.Collect(cube, nodeId);
            Assert.AreEqual(1, operations.Count);
            Assert.AreEqual(OpKinds.SetProperty, operations[0].Kind);
            Assert.AreEqual(ScenePropertyAccess.LocalPosition, operations[0].PropertyPath);
            Assert.AreEqual("0,0,0", operations[0].OldValue);
            Assert.AreEqual("1,2,3", operations[0].NewValue);

            Assert.AreEqual(0, diff.Collect(cube, nodeId).Count, "a collected change must not repeat");
        }

        [Test]
        public void RenameAndActiveToggleAreCaptured()
        {
            var cube = SaveSceneWithCube("Scalars", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var diff = new SceneDiff();
            diff.Baseline(cube, nodeId);

            cube.name = "Renamed";
            cube.SetActive(false);

            var operations = diff.Collect(cube, nodeId);
            Assert.AreEqual(2, operations.Count);
            var paths = new List<string>();
            for (var i = 0; i < operations.Count; i++)
            {
                paths.Add(operations[i].PropertyPath);
            }

            Assert.Contains(ScenePropertyAccess.Name, paths);
            Assert.Contains(ScenePropertyAccess.Active, paths);
        }

        [Test]
        public void ApplyingAnOpMovesTheObjectAndDoesNotEchoBack()
        {
            var cube = SaveSceneWithCube("Apply", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var diff = new SceneDiff();
            var applier = new SceneOpApplier(registry, diff, new SceneStructure(registry));

            diff.Baseline(cube, nodeId);

            var wire = new SceneCollab.Contracts.Protocol.WireMessage
            {
                type = SceneCollab.Contracts.Protocol.MessageTypes.Op,
                opKind = OpKinds.SetProperty,
                nodeId = nodeId,
                propertyPath = ScenePropertyAccess.LocalPosition,
                oldValue = "0,0,0",
                newValue = "4,5,6",
                displayName = "peer",
            };

            string note;
            Assert.IsTrue(applier.Apply(wire, out note), note);
            Assert.AreEqual(new Vector3(4f, 5f, 6f), cube.transform.localPosition);
            Assert.AreEqual(0, diff.Collect(cube, nodeId).Count, "the applied value echoed back as a local edit");
        }

        [Test]
        public void OpForAnUnknownNodeIsIgnoredNotGuessed()
        {
            SaveSceneWithCube("Unknown", out _);
            var registry = new SceneNodeRegistry();
            var diff = new SceneDiff();
            var applier = new SceneOpApplier(registry, diff, new SceneStructure(registry));

            var wire = new SceneCollab.Contracts.Protocol.WireMessage
            {
                type = SceneCollab.Contracts.Protocol.MessageTypes.Op,
                opKind = OpKinds.SetProperty,
                nodeId = StableNodeId.FromGlobalObjectId("GlobalObjectId_V1-2-00000000000000000000000000000000-0-0"),
                propertyPath = ScenePropertyAccess.LocalPosition,
                oldValue = "0,0,0",
                newValue = "1,1,1",
            };

            string note;
            Assert.IsFalse(applier.Apply(wire, out note));
            Assert.AreEqual(0, applier.Applied);
            Assert.AreEqual(1, applier.Ignored);
            Assert.IsNotNull(note);
        }

        [Test]
        public void OverwritingADivergedValueIsCounted()
        {
            var cube = SaveSceneWithCube("Diverge", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var diff = new SceneDiff();
            var applier = new SceneOpApplier(registry, diff, new SceneStructure(registry));

            diff.Baseline(cube, nodeId);
            cube.transform.localPosition = new Vector3(1f, 0f, 0f);
            diff.Collect(cube, nodeId);

            var wire = new SceneCollab.Contracts.Protocol.WireMessage
            {
                type = SceneCollab.Contracts.Protocol.MessageTypes.Op,
                opKind = OpKinds.SetProperty,
                nodeId = nodeId,
                propertyPath = ScenePropertyAccess.LocalPosition,
                oldValue = "0,0,0",
                newValue = "2,0,0",
            };

            string note;
            Assert.IsTrue(applier.Apply(wire, out note), note);
            Assert.AreEqual(1, applier.Overwritten, "a silent overwrite must be visible in the UI");
        }

        // ── save guard ────────────────────────────────────────────────────────

        [Test]
        public void OnlySceneFilesAreGuarded()
        {
            Assert.IsTrue(SceneSaveGuard.IsScenePath("Assets/Scenes/Main.unity"));
            Assert.IsTrue(SceneSaveGuard.IsScenePath("C:/x/Main.UNITY"));
            Assert.IsFalse(SceneSaveGuard.IsScenePath("Assets/Materials/Red.mat"));
            Assert.IsFalse(SceneSaveGuard.IsScenePath(null));
        }
    }
}
