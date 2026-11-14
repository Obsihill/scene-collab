using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SceneCollab.Editor.Tests
{
    public sealed class SceneEditingGateTests
    {
        readonly List<string> _sceneAssets = new List<string>();
        Scene _scene;
        SceneEditingGate _gate;

        [SetUp]
        public void SetUp()
        {
            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _gate = new SceneEditingGate();
        }

        [TearDown]
        public void TearDown()
        {
            _gate?.Dispose();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            foreach (var path in _sceneAssets)
            {
                AssetDatabase.DeleteAsset(path);
            }
            _sceneAssets.Clear();
        }

        Scene NewScene()
        {
            // Unity refuses additive creation alongside an unsaved, untitled dirty scene. Save only
            // the empty disposable fixture scene, before objects or temporary flags are introduced.
            Assert.IsFalse(_gate.Blocked);
            var path = AssetDatabase.GenerateUniqueAssetPath("Assets/__SceneEditingGateTests.unity");
            _sceneAssets.Add(path);
            Assert.IsTrue(EditorSceneManager.SaveScene(_scene, path));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            SceneManager.SetActiveScene(_scene);
            return scene;
        }

        static void AssertLocked(Object target, HideFlags original)
        {
            Assert.AreEqual(original | HideFlags.NotEditable, target.hideFlags, target.name);
        }

        [Test]
        public void BlockingAndRefreshingPreserveEveryObjectsExactOriginalFlags()
        {
            var root = new GameObject("Root");
            var child = new GameObject("Inactive child");
            child.transform.SetParent(root.transform);
            child.SetActive(false);
            var collider = child.AddComponent<BoxCollider>();
            root.hideFlags = HideFlags.HideInInspector;
            root.transform.hideFlags = HideFlags.DontSaveInBuild;
            child.hideFlags = HideFlags.None;
            child.transform.hideFlags = HideFlags.HideInInspector;
            collider.hideFlags = HideFlags.HideInInspector | HideFlags.DontSaveInBuild;
            var originals = new Dictionary<Object, HideFlags>
            {
                { root, root.hideFlags },
                { root.transform, root.transform.hideFlags },
                { child, child.hideFlags },
                { child.transform, child.transform.hideFlags },
                { collider, collider.hideFlags },
            };

            _gate.SetBlocked(true);
            _gate.Refresh();
            _gate.SetBlocked(true);

            Assert.IsTrue(_gate.Blocked);
            foreach (var entry in originals)
            {
                AssertLocked(entry.Key, entry.Value);
            }

            _gate.SetBlocked(false);
            _gate.SetBlocked(false);
            _gate.Refresh();

            Assert.IsFalse(_gate.Blocked);
            foreach (var entry in originals)
            {
                Assert.AreEqual(entry.Value, entry.Key.hideFlags, entry.Key.name);
            }
        }

        [Test]
        public void PreexistingReadOnlyObjectsAndComponentsStayReadOnlyAfterUnblocking()
        {
            var root = new GameObject("Already locked");
            var collider = root.AddComponent<BoxCollider>();
            root.hideFlags = HideFlags.NotEditable | HideFlags.HideInInspector;
            collider.hideFlags = HideFlags.NotEditable | HideFlags.DontSaveInBuild;
            var rootFlags = root.hideFlags;
            var colliderFlags = collider.hideFlags;
            var transformFlags = root.transform.hideFlags;

            _gate.SetBlocked(true);
            AssertLocked(root.transform, transformFlags);
            _gate.SetBlocked(false);

            Assert.AreEqual(rootFlags, root.hideFlags);
            Assert.AreEqual(colliderFlags, collider.hideFlags);
            Assert.AreEqual(transformFlags, root.transform.hideFlags);
        }

        [Test]
        public void RefreshGuardsNewChildrenRootsAndComponentsAndReassertsRemovedLocks()
        {
            var root = new GameObject("Snapshot survivor");
            root.hideFlags = HideFlags.HideInInspector;
            var transformFlags = root.transform.hideFlags;
            _gate.SetBlocked(true);

            root.hideFlags = HideFlags.None; // A snapshot can overwrite the temporary flags.
            root.transform.hideFlags = HideFlags.None;
            var child = new GameObject("New child");
            child.transform.SetParent(root.transform);
            child.SetActive(false);
            var collider = root.AddComponent<BoxCollider>();
            collider.hideFlags = HideFlags.HideInInspector;
            var newRoot = new GameObject("New root");

            _gate.Refresh();

            AssertLocked(root, HideFlags.None);
            AssertLocked(root.transform, HideFlags.None);
            AssertLocked(child, HideFlags.None);
            AssertLocked(child.transform, HideFlags.None);
            AssertLocked(collider, HideFlags.HideInInspector);
            AssertLocked(newRoot, HideFlags.None);
            _gate.SetBlocked(false);
            Assert.AreEqual(HideFlags.HideInInspector, root.hideFlags, "Keep the first capture, not the snapshot's flags.");
            Assert.AreEqual(transformFlags, root.transform.hideFlags);
            Assert.AreEqual(HideFlags.None, child.hideFlags);
            Assert.AreEqual(HideFlags.None, child.transform.hideFlags);
            Assert.AreEqual(HideFlags.HideInInspector, collider.hideFlags);
            Assert.AreEqual(HideFlags.None, newRoot.hideFlags);
        }

        [Test]
        public void DestroyedObjectsAndComponentsDoNotPreventSurvivorsFromBeingRestored()
        {
            var survivor = new GameObject("Survivor");
            var removedComponent = survivor.AddComponent<BoxCollider>();
            var removedObject = new GameObject("Removed by snapshot");
            _gate.SetBlocked(true);

            Object.DestroyImmediate(removedComponent);
            Object.DestroyImmediate(removedObject);
            Assert.DoesNotThrow(() => _gate.Refresh());

            var replacement = survivor.AddComponent<BoxCollider>();
            _gate.Refresh();
            AssertLocked(replacement, HideFlags.None);
            Object.DestroyImmediate(replacement); // Also tolerate destruction without another refresh.
            Assert.DoesNotThrow(() => _gate.SetBlocked(false));
            Assert.AreEqual(HideFlags.None, survivor.hideFlags);
            Assert.AreEqual(HideFlags.None, survivor.transform.hideFlags);
        }

        [Test]
        public void ChangingTheActiveSceneDoesNotChangeTheCapturedScope()
        {
            var otherScene = NewScene();
            var first = new GameObject("Paused scene object");
            _gate.SetBlocked(true);
            SceneManager.SetActiveScene(otherScene);
            var other = new GameObject("Other scene object");
            var addedToPausedScene = new GameObject("Late paused scene object");
            SceneManager.MoveGameObjectToScene(addedToPausedScene, _scene);

            _gate.SetBlocked(true);

            AssertLocked(first, HideFlags.None);
            AssertLocked(addedToPausedScene, HideFlags.None);
            Assert.AreEqual(HideFlags.None, other.hideFlags);
            Assert.AreEqual(HideFlags.None, other.transform.hideFlags);

            _gate.SetBlocked(false);
            _gate.SetBlocked(true); // A genuinely new pause captures the newly active scene.
            Assert.AreEqual(HideFlags.None, first.hideFlags);
            AssertLocked(other, HideFlags.None);
        }

        [Test]
        public void RefreshRestoresObjectsMovedOutOfTheCapturedScene()
        {
            var otherScene = NewScene();
            var moved = new GameObject("Moved out");
            moved.hideFlags = HideFlags.HideInInspector;
            var transformFlags = moved.transform.hideFlags;
            _gate.SetBlocked(true);
            SceneManager.MoveGameObjectToScene(moved, otherScene);

            _gate.Refresh();

            Assert.AreEqual(HideFlags.HideInInspector, moved.hideFlags);
            Assert.AreEqual(transformFlags, moved.transform.hideFlags);
            _gate.Dispose();
            Assert.AreEqual(HideFlags.HideInInspector, moved.hideFlags);
        }

        [Test]
        public void SceneClosureRestoresSurvivorsAndDoesNotAdoptTheReplacementScene()
        {
            var otherScene = NewScene();
            var moved = new GameObject("Survives in another scene");
            new GameObject("Destroyed with paused scene");
            _gate.SetBlocked(true);
            SceneManager.SetActiveScene(otherScene);
            SceneManager.MoveGameObjectToScene(moved, otherScene);

            Assert.IsTrue(EditorSceneManager.CloseScene(_scene, true));

            Assert.IsTrue(_gate.Blocked, "Only the owner changes the pause state.");
            Assert.AreEqual(HideFlags.None, moved.hideFlags);
            Assert.AreEqual(HideFlags.None, moved.transform.hideFlags);
            var replacement = new GameObject("Replacement scene");
            Assert.DoesNotThrow(() => _gate.Refresh());
            _gate.SetBlocked(true);
            Assert.AreEqual(HideFlags.None, replacement.hideFlags);
            Assert.IsFalse(_gate.ConsumeSceneInput(new Event { type = EventType.KeyDown, keyCode = KeyCode.Delete }, false));
            Assert.DoesNotThrow(() => _gate.Dispose());
        }

        [Test]
        public void DisposeRestoresFlagsAndCannotBeReactivatedByLateCallbacks()
        {
            var root = new GameObject("Owner disposed");
            root.transform.hideFlags = HideFlags.NotEditable | HideFlags.HideInInspector;
            _gate.SetBlocked(true);
            _gate.Dispose();
            _gate.Dispose();
            _gate.Refresh();
            _gate.SetBlocked(true);

            Assert.IsFalse(_gate.Blocked);
            Assert.AreEqual(HideFlags.None, root.hideFlags);
            Assert.AreEqual(HideFlags.NotEditable | HideFlags.HideInInspector, root.transform.hideFlags);
            Assert.IsFalse(_gate.ConsumeSceneInput(new Event { type = EventType.MouseDown }, false));
        }

        int SceneGuiSubscriptions()
        {
            // Inspect the actual event backing field; no editor window or fake event bus is needed.
            var field = typeof(SceneView).GetField("beforeSceneGui", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            var callbacks = field.GetValue(null) as Delegate;
            var count = 0;
            if (callbacks != null)
            {
                foreach (var callback in callbacks.GetInvocationList())
                    if (ReferenceEquals(callback.Target, _gate)) count++;
            }
            return count;
        }

        [Test]
        public void SubscriptionsAreIdempotentAndCleanupStopsGuardingNewObjects()
        {
            Assert.AreEqual(0, SceneGuiSubscriptions());
            _gate.SetBlocked(true);
            _gate.SetBlocked(true);
            Assert.AreEqual(1, SceneGuiSubscriptions());
            var added = new GameObject("Added while paused");
            _gate.Refresh(); // The owner ticks this even when batch mode delays hierarchyChanged.
            AssertLocked(added, HideFlags.None);
            AssertLocked(added.transform, HideFlags.None);

            _gate.SetBlocked(false);
            Assert.AreEqual(0, SceneGuiSubscriptions());
            var afterUnblock = new GameObject("Added after unblock");
            _gate.Refresh();
            Assert.AreEqual(HideFlags.None, afterUnblock.hideFlags);

            _gate.SetBlocked(true);
            Assert.AreEqual(1, SceneGuiSubscriptions());
            _gate.Dispose();
            Assert.AreEqual(0, SceneGuiSubscriptions());
            var afterDispose = new GameObject("Added after disposal");
            _gate.Refresh();
            Assert.AreEqual(HideFlags.None, added.hideFlags);
            Assert.AreEqual(HideFlags.None, afterUnblock.hideFlags);
            Assert.AreEqual(HideFlags.None, afterDispose.hideFlags);
        }

        [Test]
        public void TemporaryComponentFlagsNeverEnterTheDiffOrSnapshot()
        {
            var root = new GameObject("Snapshot flags");
            root.AddComponent<BoxCollider>().hideFlags = HideFlags.HideInInspector;
            var registry = new SceneNodeRegistry();
            const string node = "0123456789abcdef";
            registry.Adopt(root, node);
            var diff = new SceneDiff { Registry = registry };
            diff.Baseline(root, node);
            _gate.SetBlocked(true);

            var operations = new List<SceneOperation>();
            diff.CollectInto(root, node, operations);
            Assert.IsEmpty(operations, "A local pause must not produce shared property edits.");
            var paths = new List<string>();
            SceneComponentFields.CollectPaths(root, paths);
            Assert.IsFalse(paths.Exists(path => path.EndsWith("/m_ObjectHideFlags", StringComparison.Ordinal)));

            List<WireMessage> frames;
            string note;
            Assert.IsTrue(SceneSnapshot.TryBuild(_scene, registry, out frames, out note), note);
            var componentProperties = 0;
            foreach (var frame in frames)
            {
                if (frame.opKind != OpKinds.SnapshotChunk) continue;
                foreach (var operation in JsonUtility.FromJson<SnapshotChunk>(frame.newValue).ops)
                {
                    if (!SceneComponentFields.IsComponentPath(operation.propertyPath)) continue;
                    componentProperties++;
                    StringAssert.DoesNotContain("m_ObjectHideFlags", operation.propertyPath);
                }
            }
            Assert.Greater(componentProperties, 0, "Exercise a real component snapshot, not an empty scene.");
        }

        [Test]
        public void TerrainComponentsAreGuardedWithoutChangingTheSharedTerrainAssetFlags()
        {
            var data = new TerrainData { heightmapResolution = 33 };
            data.hideFlags = HideFlags.HideInInspector;
            try
            {
                var terrainObject = Terrain.CreateTerrainGameObject(data);
                var terrain = terrainObject.GetComponent<Terrain>();
                var collider = terrainObject.GetComponent<TerrainCollider>();
                _gate.SetBlocked(true);

                AssertLocked(terrainObject, HideFlags.None);
                AssertLocked(terrain, HideFlags.None);
                AssertLocked(collider, HideFlags.None);
                Assert.AreEqual(HideFlags.HideInInspector, data.hideFlags);

                _gate.Dispose();
                Assert.AreEqual(HideFlags.None, terrain.hideFlags);
                Assert.AreEqual(HideFlags.None, collider.hideFlags);
                Assert.AreEqual(HideFlags.HideInInspector, data.hideFlags);
                Object.DestroyImmediate(terrainObject);
            }
            finally
            {
                Object.DestroyImmediate(data);
            }
        }

        [TestCase(EventType.MouseDown)]
        [TestCase(EventType.MouseDrag)]
        [TestCase(EventType.MouseUp)]
        [TestCase(EventType.DragUpdated)]
        [TestCase(EventType.DragPerform)]
        [TestCase(EventType.ContextClick)]
        public void EditingPointerEventsAreClassifiedForConsumption(EventType type)
        {
            Assert.IsTrue(SceneEditingGate.IsEditingInput(type));
        }

        [Test]
        public void TheLiveAdapterConsumesAnEditingEvent()
        {
            _gate.SetBlocked(true);
            var evt = new Event { type = EventType.ExecuteCommand, commandName = "Delete" };
            Assert.IsTrue(_gate.ConsumeSceneInput(evt, false));
            Assert.AreEqual(EventType.Used, evt.type);
        }

        [TestCase("Delete")]
        [TestCase("SoftDelete")]
        [TestCase("Duplicate")]
        [TestCase("Cut")]
        [TestCase("Paste")]
        [TestCase("PasteAsChild")]
        [TestCase("UndoRedoPerformed")]
        [TestCase("Undo")]
        [TestCase("Redo")]
        [TestCase("CustomMutation")]
        public void EditingCommandsAreConsumedDuringBothValidationAndExecution(string command)
        {
            _gate.SetBlocked(true);
            foreach (var type in new[] { EventType.ValidateCommand, EventType.ExecuteCommand })
            {
                var evt = new Event { type = type, commandName = command };
                Assert.IsTrue(_gate.ConsumeSceneInput(evt, true), "Navigation must not exempt commands.");
                Assert.AreEqual(EventType.Used, evt.type);
            }
        }

        [TestCase(KeyCode.Delete, EventModifiers.None)]
        [TestCase(KeyCode.Backspace, EventModifiers.None)]
        [TestCase(KeyCode.D, EventModifiers.Control)]
        [TestCase(KeyCode.D, EventModifiers.Command)]
        [TestCase(KeyCode.V, EventModifiers.Control)]
        [TestCase(KeyCode.Z, EventModifiers.Command)]
        [TestCase(KeyCode.Y, EventModifiers.Control)]
        [TestCase(KeyCode.F, EventModifiers.Alt | EventModifiers.Shift)]
        [TestCase(KeyCode.Delete, EventModifiers.Alt)]
        public void DestructiveKeysStayBlockedEvenDuringCameraNavigation(KeyCode key, EventModifiers modifiers)
        {
            _gate.SetBlocked(true);
            foreach (var type in new[] { EventType.KeyDown, EventType.KeyUp })
            {
                var evt = new Event { type = type, keyCode = key, modifiers = modifiers };
                Assert.IsTrue(_gate.ConsumeSceneInput(evt, true));
                Assert.AreEqual(EventType.Used, evt.type);
            }
        }

        [TestCase(0, EventModifiers.Alt, false)]
        [TestCase(0, EventModifiers.None, true)]
        [TestCase(1, EventModifiers.None, false)]
        [TestCase(2, EventModifiers.None, false)]
        public void OrbitPanAndFlyMouseGesturesRemainAvailable(int button, EventModifiers modifiers, bool viewTool)
        {
            foreach (var type in new[] { EventType.MouseDown, EventType.MouseDrag, EventType.MouseUp })
            {
                Assert.IsFalse(SceneEditingGate.IsEditingInput(type, button, modifiers, viewToolActive: viewTool));
            }
        }

        [TestCase(EventType.ScrollWheel)]
        [TestCase(EventType.Layout)]
        [TestCase(EventType.Repaint)]
        [TestCase(EventType.MouseMove)]
        [TestCase(EventType.Used)]
        [TestCase(EventType.Ignore)]
        public void ZoomAndNonEditingEventsAreLeftUntouched(EventType type)
        {
            _gate.SetBlocked(true);
            var evt = new Event { type = type };
            Assert.IsFalse(_gate.ConsumeSceneInput(evt, false));
            Assert.AreEqual(type, evt.type);
        }

        [TestCase("FrameSelected")]
        [TestCase("FrameSelectedWithLock")]
        [TestCase("SceneViewPanFocusEventCommand")]
        [TestCase("SetSceneViewMotionHotControlEventCommand")]
        [TestCase("Copy")]
        [TestCase("SelectAll")]
        public void NavigationAndReadOnlyCommandsRemainAvailable(string command)
        {
            _gate.SetBlocked(true);
            foreach (var type in new[] { EventType.ValidateCommand, EventType.ExecuteCommand })
            {
                var evt = new Event { type = type, commandName = command };
                Assert.IsFalse(_gate.ConsumeSceneInput(evt, false));
                Assert.AreEqual(type, evt.type);
            }
        }

        [TestCase(KeyCode.W)]
        [TestCase(KeyCode.A)]
        [TestCase(KeyCode.S)]
        [TestCase(KeyCode.D)]
        [TestCase(KeyCode.E)]
        [TestCase(KeyCode.UpArrow)]
        [TestCase(KeyCode.DownArrow)]
        [TestCase(KeyCode.LeftArrow)]
        [TestCase(KeyCode.RightArrow)]
        public void FlyKeysAreAllowedOnlyWithTheViewToolActive(KeyCode key)
        {
            _gate.SetBlocked(true);
            foreach (var type in new[] { EventType.KeyDown, EventType.KeyUp })
            {
                var navigation = new Event { type = type, keyCode = key, modifiers = EventModifiers.Shift };
                Assert.IsFalse(_gate.ConsumeSceneInput(navigation, true));
                Assert.AreEqual(type, navigation.type);
                var editing = new Event { type = type, keyCode = key };
                Assert.IsTrue(_gate.ConsumeSceneInput(editing, false));
            }
        }

        [TestCase(KeyCode.Q)]
        [TestCase(KeyCode.F)]
        [TestCase(KeyCode.Escape)]
        [TestCase(KeyCode.LeftAlt)]
        [TestCase(KeyCode.LeftShift)]
        public void FramingHandToolAndNavigationModifiersRemainAvailable(KeyCode key)
        {
            _gate.SetBlocked(true);
            var evt = new Event { type = EventType.KeyDown, keyCode = key };
            Assert.IsFalse(_gate.ConsumeSceneInput(evt, false));
            Assert.AreEqual(EventType.KeyDown, evt.type);
        }

        [Test]
        public void AnUnblockedGateDoesNotConsumeInputOrChangeFlags()
        {
            var root = new GameObject("Unblocked");
            _gate.Refresh();
            _gate.SetBlocked(false);
            var evt = new Event { type = EventType.ExecuteCommand, commandName = "Delete" };

            Assert.IsFalse(_gate.Blocked);
            Assert.AreEqual(HideFlags.None, root.hideFlags);
            Assert.IsFalse(_gate.ConsumeSceneInput(evt, false));
            Assert.AreEqual(EventType.ExecuteCommand, evt.type);
            Assert.IsFalse(_gate.ConsumeSceneInput(null, false));
        }
    }
}
