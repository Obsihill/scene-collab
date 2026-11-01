using System;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Reads and writes the properties the session synchronises, always through
    /// <see cref="SerializedObject"/>/<see cref="SerializedProperty"/>.
    ///
    /// Why not <c>transform.position = …</c>: the serialized layer is what the scene file actually
    /// contains, so writing through it produces exactly the delta a save would write. Going through
    /// the runtime API instead leaves prefab overrides and <c>m_LocalEulerAnglesHint</c> in states
    /// the file does not agree with, which shows up later as "my position moved back after save".
    ///
    /// Two address spaces share this entry point: the object's own fields (<c>m_Name</c>,
    /// <c>m_LocalPosition</c>) and component fields (<c>c/Type#slot/field</c>). The slot is what makes
    /// the second AudioSource reachable; see <see cref="SceneComponentFields"/>.
    /// </summary>
    public static class ScenePropertyAccess
    {
        public const string Name = "m_Name";
        public const string Active = "m_IsActive";
        public const string LocalPosition = "m_LocalPosition";
        public const string LocalRotation = "m_LocalRotation";
        public const string LocalScale = "m_LocalScale";

        /// <summary>The property set kept in sync. Anything not here is deliberately not synced.</summary>
        public static readonly string[] TrackedPaths =
        {
            Name, Active, LocalPosition, LocalRotation, LocalScale,
        };

        public static bool IsTracked(string path)
        {
            if (SceneComponentFields.IsComponentPath(path))
            {
                return true;
            }

            for (var i = 0; i < TrackedPaths.Length; i++)
            {
                if (string.Equals(TrackedPaths[i], path, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool TryRead(GameObject gameObject, string path, out string value)
        {
            return TryRead(gameObject, path, null, out value);
        }

        public static bool TryRead(GameObject gameObject, string path, SceneNodeRegistry registry, out string value)
        {
            value = null;
            if (gameObject == null || !IsTracked(path))
            {
                return false;
            }

            SerializedProperty property;
            if (SceneComponentFields.IsComponentPath(path))
            {
                property = FindComponentProperty(gameObject, path);
            }
            else
            {
                var target = IsOnGameObject(path) ? (UnityEngine.Object)gameObject : gameObject.transform;
                property = new SerializedObject(target).FindProperty(path);
            }

            if (property == null)
            {
                return false;
            }

            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                return SceneReferenceCodec.TryRead(property, registry, out value);
            }

            return SceneValueCodec.TryRead(property, out value);
        }

        public static bool TryWrite(GameObject gameObject, string path, string value)
        {
            string problem;
            return TryWrite(gameObject, path, null, value, out problem);
        }

        /// <summary>
        /// Writes one field. A false return fills <paramref name="problem"/> — callers surface it rather
        /// than letting the two editors drift apart silently.
        /// </summary>
        public static bool TryWrite(GameObject gameObject, string path, SceneNodeRegistry registry,
            string value, out string problem)
        {
            problem = null;

            if (gameObject == null || !IsTracked(path))
            {
                problem = "untracked field " + path;
                return false;
            }

            SerializedObject serialized;
            SerializedProperty property;

            if (SceneComponentFields.IsComponentPath(path))
            {
                string typeName, serializedPath;
                int slot;
                if (!SceneComponentFields.TryParse(path, out typeName, out slot, out serializedPath))
                {
                    problem = "malformed component path " + path;
                    return false;
                }

                var component = SceneComponentFields.FindComponent(gameObject, typeName, slot);
                if (component == null)
                {
                    problem = "missing " + SceneReferenceCodec.ShortTypeName(typeName) + " #" + slot;
                    return false;
                }

                serialized = new SerializedObject(component);
                property = serialized.FindProperty(serializedPath);
            }
            else
            {
                var target = IsOnGameObject(path) ? (UnityEngine.Object)gameObject : gameObject.transform;
                serialized = new SerializedObject(target);
                property = serialized.FindProperty(path);
            }

            if (property == null)
            {
                problem = "no field " + path;
                return false;
            }

            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                string referenceProblem;
                if (!SceneReferenceCodec.TryWrite(property, value, registry, out referenceProblem))
                {
                    problem = referenceProblem;
                    return false;
                }
            }
            else if (!SceneValueCodec.TryWrite(property, value))
            {
                problem = "value does not fit " + path + ": " + value;
                return false;
            }

            // WithoutUndo: a remote edit is not this user's undo step. Recording it would make Ctrl+Z
            // undo a colleague's change locally while the server still believes it happened.
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        /// <summary>Which serialized field a component path addresses, or null when the component is absent.</summary>
        static SerializedProperty FindComponentProperty(GameObject gameObject, string path)
        {
            string typeName, serializedPath;
            int slot;
            if (!SceneComponentFields.TryParse(path, out typeName, out slot, out serializedPath))
            {
                return null;
            }

            var component = SceneComponentFields.FindComponent(gameObject, typeName, slot);
            if (component == null)
            {
                return null;
            }

            return new SerializedObject(component).FindProperty(serializedPath);
        }

        static bool IsOnGameObject(string path)
        {
            return string.Equals(path, Name, StringComparison.Ordinal)
                || string.Equals(path, Active, StringComparison.Ordinal);
        }
    }
}
