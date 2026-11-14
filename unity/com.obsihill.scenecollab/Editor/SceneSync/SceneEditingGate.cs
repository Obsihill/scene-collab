using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Temporarily makes the scene that was active at pause time read-only in the normal editor UI.
    /// The owner must block saving while paused and call Refresh after applying a snapshot. Original
    /// flags live only in this instance; no Undo records, scene markers or assets are written.
    /// This is an editing guard, not a veto on scripts, custom editors or direct TerrainData writes.
    /// Use on the editor thread, and dispose when the owning session ends.
    /// </summary>
    internal sealed class SceneEditingGate : IDisposable
    {
        readonly Dictionary<Object, HideFlags> _originalFlags = new Dictionary<Object, HideFlags>();
        readonly Dictionary<Object, HideFlags> _retired = new Dictionary<Object, HideFlags>();
        readonly Dictionary<Object, HideFlags> _guardFlags = new Dictionary<Object, HideFlags>();
        readonly List<GameObject> _roots = new List<GameObject>();
        readonly List<Component> _components = new List<Component>();
        Scene _scene;
        bool _attached;
        bool _disposed;

        public bool Blocked { get; private set; }

        public void SetBlocked(bool blocked)
        {
            // A late owner callback must not reattach a disposed gate.
            if (_disposed)
            {
                return;
            }

            if (Blocked == blocked)
            {
                if (blocked)
                {
                    Refresh();
                }
                return;
            }

            Blocked = blocked;
            if (blocked)
            {
                _scene = SceneManager.GetActiveScene();
                Attach();
                Refresh();
            }
            else
            {
                Detach();
                RestoreFlags();
                _scene = default;
            }

            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        /// <summary>Guards new objects/components without replacing any previously captured flags.</summary>
        public void Refresh()
        {
            if (!Blocked)
            {
                return;
            }

            if (!_scene.IsValid() || !_scene.isLoaded)
            {
                RestoreFlags();
                _scene = default;
                return;
            }

            // Do not retain destroyed wrappers, or leave locks on objects moved to another scene.
            foreach (var entry in _originalFlags)
            {
                var target = entry.Key;
                var gameObject = target as GameObject;
                if (target is Component component && component != null)
                {
                    gameObject = component.gameObject;
                }

                if (gameObject == null || gameObject.scene != _scene)
                {
                    _retired.Add(target, entry.Value);
                }
            }

            ApplyFlags(_retired);
            foreach (var target in _retired.Keys)
            {
                _originalFlags.Remove(target);
            }
            _retired.Clear();

            _scene.GetRootGameObjects(_roots);
            foreach (var root in _roots)
            {
                // Include inactive children and components, including Transform and Terrain.
                root.GetComponentsInChildren(true, _components);
                foreach (var component in _components)
                {
                    if (component != null) // Missing scripts are returned as null.
                    {
                        Capture(component.gameObject);
                        Capture(component);
                    }
                }
            }
            // A GameObject flag assignment also writes its components. Capture the whole scene
            // before changing anything, then reapply each component's own flags after its owner.
            ApplyFlags(_guardFlags);
            _guardFlags.Clear();
            _roots.Clear();
            _components.Clear();
        }

        void Capture(Object target)
        {
            var flags = target.hideFlags;
            if (!_originalFlags.ContainsKey(target))
            {
                _originalFlags.Add(target, flags);
            }

            _guardFlags[target] = flags | HideFlags.NotEditable;
        }

        static void Restore(Object target, HideFlags flags)
        {
            if (target != null && target.hideFlags != flags)
            {
                target.hideFlags = flags;
            }
        }

        static void ApplyFlags(Dictionary<Object, HideFlags> flags)
        {
            // Never depend on dictionary order: restoring an owner after its components would
            // overwrite their original flags, including locks that existed before the pause.
            foreach (var entry in flags)
            {
                if (entry.Key is GameObject) Restore(entry.Key, entry.Value);
            }
            foreach (var entry in flags)
            {
                if (entry.Key is Component) Restore(entry.Key, entry.Value);
            }
        }

        void RestoreFlags()
        {
            ApplyFlags(_originalFlags);
            _originalFlags.Clear();
            _retired.Clear();
            _guardFlags.Clear();
            _roots.Clear();
            _components.Clear();
        }

        void Attach()
        {
            if (_attached)
            {
                return;
            }

            // Unity 6 (the package minimum) exposes this before built-in object/terrain tools run.
            SceneView.beforeSceneGui += OnBeforeSceneGui;
            EditorApplication.hierarchyChanged += Refresh;
            Undo.undoRedoPerformed += Refresh;
            EditorSceneManager.sceneClosed += OnSceneClosed;
            AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            EditorApplication.quitting += Dispose;
            _attached = true;
        }

        void Detach()
        {
            if (!_attached)
            {
                return;
            }

            SceneView.beforeSceneGui -= OnBeforeSceneGui;
            EditorApplication.hierarchyChanged -= Refresh;
            Undo.undoRedoPerformed -= Refresh;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            EditorApplication.quitting -= Dispose;
            _attached = false;
        }

        void OnSceneClosed(Scene scene)
        {
            if (scene == _scene)
            {
                RestoreFlags();
                _scene = default;
                // The owner still controls Blocked. Never silently adopt a replacement active scene.
            }
        }

        void OnBeforeSceneGui(SceneView view)
        {
            var evt = Event.current;
            var drag = evt != null && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform);
            if (ConsumeSceneInput(evt, Tools.viewToolActive))
            {
                // Cancel a handle/stroke that already owned the mouse when the pause began.
                GUIUtility.hotControl = 0;
                GUIUtility.keyboardControl = 0;
                if (drag)
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                }
            }
        }

        /// <summary>Consumes a live event only when the captured scene is still paused.</summary>
        internal bool ConsumeSceneInput(Event evt, bool viewToolActive)
        {
            if (!Blocked || !_scene.IsValid() || !_scene.isLoaded || evt == null
                || !IsEditingInput(evt.type, evt.button, evt.modifiers, evt.keyCode, evt.commandName, viewToolActive))
            {
                return false;
            }

            evt.Use();
            return true;
        }

        /// <summary>
        /// Pure input policy. Synthetic pointer Event.type can read Ignore outside an IMGUI context,
        /// so tests pass the event values directly. The live adapter deliberately honors type rather
        /// than rawType: an already used or ignored event must not be revived.
        /// </summary>
        internal static bool IsEditingInput(EventType type, int button = 0,
            EventModifiers modifiers = EventModifiers.None, KeyCode keyCode = KeyCode.None,
            string commandName = null, bool viewToolActive = false)
        {
            // SceneViews can hit the paused terrain even with a selection in another additive scene.
            // Keep navigation available, but suppress editing input in every view while it is loaded.
            switch (type)
            {
                case EventType.MouseDown:
                case EventType.MouseDrag:
                case EventType.MouseUp:
                    return button != 1 && button != 2 && (modifiers & EventModifiers.Alt) == 0 && !viewToolActive;
                case EventType.DragUpdated:
                case EventType.DragPerform:
                case EventType.ContextClick:
                    return true;
                case EventType.ValidateCommand:
                case EventType.ExecuteCommand:
                    switch (commandName)
                    {
                        case "Copy":
                        case "SelectAll":
                        case "Find":
                        case "FrameSelected":
                        case "FrameSelectedWithLock":
                        // Unity's pan/fly navigation sends these command events to its SceneView.
                        case "SceneViewPanFocusEventCommand":
                        case "SetSceneViewMotionHotControlEventCommand":
                            return false;
                    }
                    return true;
                case EventType.KeyDown:
                case EventType.KeyUp:
                    return !IsNavigationKey(keyCode, modifiers, viewToolActive);
                default:
                    // In particular, never Use Layout/Repaint. ScrollWheel remains camera zoom.
                    return false;
            }

        }

        static bool IsNavigationKey(KeyCode keyCode, EventModifiers modifiers, bool viewToolActive)
        {
            switch (keyCode)
            {
                case KeyCode.LeftAlt:
                case KeyCode.RightAlt:
                case KeyCode.LeftShift:
                case KeyCode.RightShift:
                case KeyCode.LeftControl:
                case KeyCode.RightControl:
                case KeyCode.LeftCommand:
                case KeyCode.RightCommand:
                case KeyCode.Escape:
                    return true;
            }

            // Modifier shortcuts such as Ctrl/Cmd+D, Z, V, and Alt+Shift+F must not become edits.
            if ((modifiers & (EventModifiers.Control | EventModifiers.Command | EventModifiers.Alt)) != 0)
            {
                return false;
            }

            switch (keyCode)
            {
                case KeyCode.Q: // Select the hand tool (or fly down).
                case KeyCode.F: // Frame selection, with optional Shift to lock the view.
                    return true;
                case KeyCode.W:
                case KeyCode.A:
                case KeyCode.S:
                case KeyCode.D:
                case KeyCode.E:
                case KeyCode.UpArrow:
                case KeyCode.DownArrow:
                case KeyCode.LeftArrow:
                case KeyCode.RightArrow:
                case KeyCode.PageUp:
                case KeyCode.PageDown:
                    return viewToolActive;
                default:
                    return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            SetBlocked(false);
            _disposed = true;
        }
    }
}
