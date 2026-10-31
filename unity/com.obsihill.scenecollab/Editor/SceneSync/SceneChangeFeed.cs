using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Watches local edits and remembers which objects were touched.
    ///
    /// It deliberately does not try to interpret the edit. <c>Undo.postprocessModifications</c>
    /// reports the property path Unity recorded, but that path is the *inspector*'s path and does not
    /// always match the serialized name (<c>m_LocalPosition.x</c> versus <c>m_LocalPosition</c>), so
    /// using it as the op is a trap. The feed only answers "this object is dirty"; <see cref="SceneDiff"/>
    /// then reads the object's real serialized values.
    /// </summary>
    public sealed class SceneChangeFeed
    {
        readonly HashSet<GameObject> _dirty = new HashSet<GameObject>();

        public bool IsAttached { get; private set; }

        /// <summary>
        /// True when the hierarchy itself changed since the last <see cref="Drain"/>: objects added,
        /// removed, re-parented or reordered. Property modifications cannot report this reliably —
        /// an undo of a creation carries no property modification at all — so the feed listens to
        /// <c>EditorApplication.hierarchyChanged</c> instead of guessing.
        /// </summary>
        public bool StructureDirty { get; private set; }

        /// <summary>Raised after an undo/redo, when every tracked object must be re-read.</summary>
        public event System.Action UndoRedoPerformed;

        public void Attach()
        {
            if (IsAttached)
            {
                return;
            }

            Undo.postprocessModifications += OnPostprocessModifications;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            IsAttached = true;
        }

        public void Detach()
        {
            if (!IsAttached)
            {
                return;
            }

            Undo.postprocessModifications -= OnPostprocessModifications;
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            IsAttached = false;
            StructureDirty = false;
            _dirty.Clear();
        }

        public void MarkDirty(GameObject gameObject)
        {
            if (gameObject != null)
            {
                _dirty.Add(gameObject);
            }
        }

        /// <summary>Hands over the objects touched since the last call and clears the set.</summary>
        public void Drain(List<GameObject> into)
        {
            StructureDirty = false;

            if (_dirty.Count == 0)
            {
                return;
            }

            foreach (var gameObject in _dirty)
            {
                if (gameObject != null)
                {
                    into.Add(gameObject);
                }
            }

            _dirty.Clear();
        }

        UndoPropertyModification[] OnPostprocessModifications(UndoPropertyModification[] modifications)
        {
            if (modifications == null)
            {
                return null;
            }

            for (var i = 0; i < modifications.Length; i++)
            {
                var modification = modifications[i];
                var current = modification.currentValue;

                // A property modification without a target is a structural change; v0.2 ignores
                // those (they need the object lock, which is v0.3).
                if (current == null || current.target == null)
                {
                    continue;
                }

                var gameObject = ToGameObject(current.target);
                if (gameObject != null)
                {
                    _dirty.Add(gameObject);
                }
            }

            // Always return the array untouched: filtering here would cancel the user's edit.
            return modifications;
        }

        void OnHierarchyChanged()
        {
            StructureDirty = true;
        }

        void OnUndoRedo()
        {
            var handler = UndoRedoPerformed;
            if (handler != null)
            {
                handler();
            }
        }

        public static GameObject ToGameObject(Object target)
        {
            var gameObject = target as GameObject;
            if (gameObject != null)
            {
                return gameObject;
            }

            var component = target as Component;
            return component == null ? null : component.gameObject;
        }
    }
}
