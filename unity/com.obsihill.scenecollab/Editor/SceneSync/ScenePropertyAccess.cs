using System;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Reads and writes the handful of properties v0.2 synchronises, always through
    /// <see cref="SerializedObject"/>/<see cref="SerializedProperty"/>.
    ///
    /// Why not <c>transform.position = …</c>: the serialized layer is what the scene file actually
    /// contains, so writing through it produces exactly the delta a save would write. Going through
    /// the runtime API instead leaves prefab overrides and <c>m_LocalEulerAnglesHint</c> in states
    /// the file does not agree with, which shows up later as "my position moved back after save".
    /// </summary>
    public static class ScenePropertyAccess
    {
        public const string Name = "m_Name";
        public const string Active = "m_IsActive";
        public const string LocalPosition = "m_LocalPosition";
        public const string LocalRotation = "m_LocalRotation";
        public const string LocalScale = "m_LocalScale";

        /// <summary>The property set v0.2 keeps in sync. Anything not here is deliberately not synced.</summary>
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
            value = null;
            if (gameObject == null || !IsTracked(path))
            {
                return false;
            }

            if (SceneComponentFields.IsComponentPath(path))
            {
                return TryReadComponent(gameObject, path, out value);
            }

            UnityEngine.Object target = IsOnGameObject(path) ? (UnityEngine.Object)gameObject : gameObject.transform;
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(path);
            if (property == null)
            {
                return false;
            }

            return SceneValueCodec.TryRead(property, out value);
        }

        public static bool TryWrite(GameObject gameObject, string path, string value)
        {
            if (gameObject == null || !IsTracked(path))
            {
                return false;
            }

            if (SceneComponentFields.IsComponentPath(path))
            {
                return TryWriteComponent(gameObject, path, value);
            }

            UnityEngine.Object target = IsOnGameObject(path) ? (UnityEngine.Object)gameObject : gameObject.transform;
            var serialized = new SerializedObject(target);
            var property = serialized.FindProperty(path);
            if (property == null)
            {
                return false;
            }

            if (!SceneValueCodec.TryWrite(property, value))
            {
                return false;
            }

            // WithoutUndo: a remote edit is not this user's undo step. Recording it would make Ctrl+Z
            // undo a colleague's change locally while the server still believes it happened.
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        static bool TryReadComponent(GameObject gameObject, string path, out string value)
        {
            value = null;

            string typeName, serializedPath;
            if (!SceneComponentFields.TryParse(path, out typeName, out serializedPath))
            {
                return false;
            }

            var component = SceneComponentFields.FindComponent(gameObject, typeName);
            if (component == null)
            {
                return false;
            }

            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(serializedPath);
            if (property == null)
            {
                return false;
            }

            return SceneValueCodec.TryRead(property, out value);
        }

        static bool TryWriteComponent(GameObject gameObject, string path, string value)
        {
            string typeName, serializedPath;
            if (!SceneComponentFields.TryParse(path, out typeName, out serializedPath))
            {
                return false;
            }

            var component = SceneComponentFields.FindComponent(gameObject, typeName);
            if (component == null)
            {
                return false;
            }

            var serialized = new SerializedObject(component);
            var property = serialized.FindProperty(serializedPath);
            if (property == null)
            {
                return false;
            }

            if (!SceneValueCodec.TryWrite(property, value))
            {
                return false;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            return true;
        }

        static bool IsOnGameObject(string path)
        {
            return string.Equals(path, Name, StringComparison.Ordinal)
                || string.Equals(path, Active, StringComparison.Ordinal);
        }
    }
}
