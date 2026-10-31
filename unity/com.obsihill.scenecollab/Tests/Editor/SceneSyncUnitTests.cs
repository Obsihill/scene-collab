using System.Collections.Generic;
using System.IO;
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
        public void ChangingAnArrayFieldProducesOneOpWithTheWholeArray()
        {
            var cube = SaveSceneWithCube("ArrayDiff", out _);
            var line = cube.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.SetPositions(new[] { Vector3.zero, Vector3.one });

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var diff = new SceneDiff();

            diff.Baseline(cube, nodeId);
            Assert.AreEqual(0, diff.Collect(cube, nodeId).Count, "baseline must not emit anything");

            line.SetPositions(new[] { Vector3.zero, new Vector3(5f, 5f, 5f) });

            var operations = diff.Collect(cube, nodeId);
            var operation = operations.FirstOrDefault(o => o.PropertyPath.EndsWith("/m_Positions", System.StringComparison.Ordinal));
            Assert.IsNotNull(operation, "no array op; got: " + string.Join(", ", operations.Select(o => o.PropertyPath)));
            Assert.AreEqual(OpKinds.SetProperty, operation.Kind);
            Assert.AreEqual("2;0,0,0;1,1,1", operation.OldValue);
            Assert.AreEqual("2;0,0,0;5,5,5", operation.NewValue);

            // The same op applied on another object must land as one array write, not a no-op.
            var other = SaveSceneWithCube("ArrayApply", out _);
            var otherLine = other.AddComponent<LineRenderer>();
            otherLine.positionCount = 2;
            otherLine.SetPositions(new[] { Vector3.one, Vector3.one });
            Assert.IsTrue(ScenePropertyAccess.TryWrite(other, operation.PropertyPath, operation.NewValue));
            Assert.AreEqual(new Vector3(5f, 5f, 5f), otherLine.GetPosition(1));
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

        // ── components: add / remove (v0.4) ───────────────────────────────────

        [Test]
        public void AddingAComponentIsCapturedAsAnOp()
        {
            var cube = SaveSceneWithCube("CompAdd", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var structure = new SceneStructure(registry);

            structure.Baseline(cube, nodeId);
            Assert.AreEqual(0, structure.CollectComponentChanges(cube, nodeId).Count, "baseline must not emit");

            cube.AddComponent<AudioSource>();

            var operations = structure.CollectComponentChanges(cube, nodeId);
            Assert.AreEqual(1, operations.Count, "one component added, one op");
            Assert.AreEqual(OpKinds.AddComponent, operations[0].Kind);
            Assert.AreEqual(typeof(AudioSource).FullName, operations[0].ComponentType);

            Assert.AreEqual(0, structure.CollectComponentChanges(cube, nodeId).Count, "a collected change must not repeat");
        }

        [Test]
        public void RemovingAComponentIsCapturedAsAnOp()
        {
            var cube = SaveSceneWithCube("CompRemove", out _);
            cube.AddComponent<AudioSource>();

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var structure = new SceneStructure(registry);

            structure.Baseline(cube, nodeId);
            Object.DestroyImmediate(cube.GetComponent<AudioSource>());

            var operations = structure.CollectComponentChanges(cube, nodeId);
            Assert.AreEqual(1, operations.Count, "one component removed, one op");
            Assert.AreEqual(OpKinds.RemoveComponent, operations[0].Kind);
            Assert.AreEqual(typeof(AudioSource).FullName, operations[0].ComponentType);
            Assert.AreEqual(typeof(AudioSource).FullName, operations[0].OldValue);
        }

        [Test]
        public void TransformIsNeverReportedAsRemoved()
        {
            var cube = SaveSceneWithCube("CompTransform", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var structure = new SceneStructure(registry);
            structure.Baseline(cube, nodeId);

            var operations = structure.CollectComponentChanges(cube, nodeId);
            Assert.AreEqual(0, operations.Count);
        }

        [Test]
        public void RemovingAComponentOnTheReceivingEditorDestroysIt()
        {
            var cube = SaveSceneWithCube("CompApply", out _);
            cube.AddComponent<AudioSource>();

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var diff = new SceneDiff();
            var applier = new SceneOpApplier(registry, diff, new SceneStructure(registry));
            diff.Baseline(cube, nodeId);

            var wire = new SceneCollab.Contracts.Protocol.WireMessage
            {
                type = SceneCollab.Contracts.Protocol.MessageTypes.Op,
                opKind = OpKinds.RemoveComponent,
                nodeId = nodeId,
                componentType = typeof(AudioSource).FullName,
                oldValue = typeof(AudioSource).FullName,
                newValue = "",
            };

            string note;
            Assert.IsTrue(applier.Apply(wire, out note), note);
            Assert.IsNull(cube.GetComponent<AudioSource>(), "the component survived a remove_component");
            Assert.AreEqual(1, applier.Applied);
        }

        [Test]
        public void RemovingAComponentTwiceIsAppliedNotFailed()
        {
            var cube = SaveSceneWithCube("CompReplay", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));

            var wire = new SceneCollab.Contracts.Protocol.WireMessage
            {
                type = SceneCollab.Contracts.Protocol.MessageTypes.Op,
                opKind = OpKinds.RemoveComponent,
                nodeId = nodeId,
                componentType = typeof(AudioSource).FullName,
                oldValue = typeof(AudioSource).FullName,
                newValue = "",
            };

            string note;
            Assert.IsTrue(applier.Apply(wire, out note), "a replay after reconnect must not fail: " + note);
            Assert.AreEqual(1, applier.Applied);
            Assert.AreEqual(0, applier.Ignored);
        }

        [Test]
        public void AddingAMissingScriptLeavesALabelledStandIn()
        {
            var cube = SaveSceneWithCube("CompMissing", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));

            var wire = new SceneCollab.Contracts.Protocol.WireMessage
            {
                type = SceneCollab.Contracts.Protocol.MessageTypes.Op,
                opKind = OpKinds.AddComponent,
                nodeId = nodeId,
                propertyPath = "component",
                componentType = "Some.Package.That.IsNot.Installed",
                oldValue = "",
                newValue = "Some.Package.That.IsNot.Installed",
            };

            string note;
            Assert.IsTrue(applier.Apply(wire, out note), note);
            Assert.AreEqual(1, applier.MissingScripts);

            var markers = cube.GetComponentsInChildren<CollabMissingMarker>(true);
            Assert.AreEqual(1, markers.Length, "no stand-in was attached for the absent script");
            StringAssert.Contains("Some.Package.That.IsNot.Installed", markers[0].label);
        }

        // ── prefab overrides (v0.4) ───────────────────────────────────────────

        [Test]
        public void PrefabOverrideIsRecordedInTheInstanceModificationList()
        {
            var cube = SaveSceneWithCube("PrefabOverride", out _);

            // A prefab asset built from this object, then re-instantiated so the test owns a real
            // prefab instance: RecordPrefabInstancePropertyModifications does nothing on a plain object.
            var prefabPath = TempFolder + "/PrefabOverride_" + _savedScenes + ".prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(cube, prefabPath);
            _createdAssets.Add(prefabPath);
            Assert.IsNotNull(prefab);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, _scene);
            instance.name = "Override Instance";

            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(instance);
            var diff = new SceneDiff();
            var applier = new SceneOpApplier(registry, diff, new SceneStructure(registry));
            diff.Baseline(instance, nodeId);

            var wire = new SceneCollab.Contracts.Protocol.WireMessage
            {
                type = SceneCollab.Contracts.Protocol.MessageTypes.Op,
                opKind = OpKinds.SetPrefabOverride,
                nodeId = nodeId,
                propertyPath = ScenePropertyAccess.LocalPosition,
                oldValue = "0,0,0",
                newValue = "7,8,9",
            };

            string note;
            Assert.IsTrue(applier.Apply(wire, out note), note);
            Assert.AreEqual(1, applier.OverridesRecorded);
            Assert.AreEqual("7,8,9", ScenePropertyAccess.TryRead(instance, ScenePropertyAccess.LocalPosition, out var read) ? read : null);

            var modifications = PrefabUtility.GetPropertyModifications(instance);
            if (modifications == null || modifications.Length == 0)
            {
                modifications = PrefabUtility.GetPropertyModifications(instance.transform);
            }
            var found = false;
            if (modifications != null)
            {
                for (var i = 0; i < modifications.Length; i++)
                {
                    if (modifications[i].propertyPath != null && modifications[i].propertyPath.StartsWith(ScenePropertyAccess.LocalPosition, System.StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }
            }

            Assert.IsTrue(found, "the override never reached the instance modification list");
        }

        [Test]
        public void PrefabOverrideOnAPlainObjectIsRefusedNotApplied()
        {
            var cube = SaveSceneWithCube("PrefabPlain", out _);
            var registry = new SceneNodeRegistry();
            var nodeId = registry.EnsureNodeId(cube);
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));

            var wire = new SceneCollab.Contracts.Protocol.WireMessage
            {
                type = SceneCollab.Contracts.Protocol.MessageTypes.Op,
                opKind = OpKinds.SetPrefabOverride,
                nodeId = nodeId,
                propertyPath = ScenePropertyAccess.LocalPosition,
                oldValue = "0,0,0",
                newValue = "1,1,1",
            };

            string note;
            Assert.IsFalse(applier.Apply(wire, out note));
            Assert.AreEqual(1, applier.Ignored);
            Assert.AreEqual(new Vector3(0f, 0f, 0f), cube.transform.localPosition);
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
