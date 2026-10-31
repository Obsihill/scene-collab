using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Component fields: which ones can travel, and how they are named on the wire.
    ///
    /// A component field is addressed as <c>c/&lt;TypeFullName&gt;/&lt;serialized path&gt;</c>. That is
    /// deliberately type-addressed rather than index-addressed: two editors can have their components in
    /// a different order, and a field op that means "the third component" would land on the wrong one.
    ///
    /// Only values the codec can express exactly are tracked — numbers, bools, strings, enums, colours,
    /// vectors. Object references are not: a material reference is a guid that either resolves or does
    /// not, and that problem belongs to the asset manifest, not to a property op.
    /// </summary>
    public static class SceneComponentFields
    {
        public const string Prefix = "c/";

        /// <summary>Upper bound on fields read per object, so one heavy component cannot flood the outbox.</summary>
        public const int MaxFieldsPerObject = 128;

        static readonly HashSet<string> SkipTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "UnityEngine.Transform",
            "UnityEngine.RectTransform",
        };

        static readonly HashSet<string> SkipPaths = new HashSet<string>(StringComparer.Ordinal)
        {
            "m_Script",
            "m_GameObject",
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
        };

        /// <summary>Paths under <paramref name="prefix"/> for every trackable field of an object.</summary>
        public static void CollectPaths(GameObject gameObject, List<string> into)
        {
            if (gameObject == null)
            {
                return;
            }

            var components = gameObject.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    // Missing script: nothing to read, and the asset health layer reports it.
                    continue;
                }

                var type = component.GetType();
                var typeName = type.FullName;
                if (typeName == null || SkipTypes.Contains(typeName))
                {
                    continue;
                }

                SerializedObject serialized;
                try
                {
                    serialized = new SerializedObject(component);
                }
                catch (Exception)
                {
                    continue;
                }

                var property = serialized.GetIterator();
                var enterChildren = true;
                while (property.NextVisible(enterChildren))
                {
                    enterChildren = true;

                    var path = property.propertyPath;

                    // Arrays travel as one value (count + elements) when they are small and hold
                    // scalars. Index-addressed element paths are not stable between editors, and a
                    // terrain-sized array is megabytes of noise, so the rest stay unsynced.
                    if (SkipPaths.Contains(path))
                    {
                        enterChildren = false;
                        continue;
                    }

                    if (property.isArray)
                    {
                        enterChildren = false;
                        if (!IsSyncableArray(property))
                        {
                            continue;
                        }

                        into.Add(Prefix + typeName + "/" + path);
                        if (into.Count >= MaxFieldsPerObject)
                        {
                            return;
                        }

                        continue;
                    }

                    if (!property.hasVisibleChildren)
                    {
                        if (!IsSyncable(property.propertyType))
                        {
                            continue;
                        }

                        into.Add(Prefix + typeName + "/" + path);
                        if (into.Count >= MaxFieldsPerObject)
                        {
                            return;
                        }
                    }
                }
            }
        }

        public static bool IsSyncable(SerializedPropertyType type)
        {
            switch (type)
            {
                case SerializedPropertyType.Float:
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Boolean:
                case SerializedPropertyType.String:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.Color:
                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Quaternion:
                case SerializedPropertyType.Vector2Int:
                case SerializedPropertyType.Vector3Int:
                case SerializedPropertyType.Rect:
                case SerializedPropertyType.RectInt:
                case SerializedPropertyType.Bounds:
                case SerializedPropertyType.BoundsInt:
                case SerializedPropertyType.LayerMask:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// A small array of scalars is one syncable value; an empty or oversized one is not, because
        /// there is nothing to compare and no bound on the payload.
        /// </summary>
        public static bool IsSyncableArray(SerializedProperty property)
        {
            if (property == null || !property.isArray)
            {
                return false;
            }

            var count = property.arraySize;
            if (count <= 0 || count > SceneValueCodec.MaxArrayElements)
            {
                return false;
            }

            return IsSyncable(property.GetArrayElementAtIndex(0).propertyType);
        }

        public static bool IsComponentPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>Splits <c>c/Type/field</c> into its two halves.</summary>
        public static bool TryParse(string path, out string typeName, out string serializedPath)
        {
            typeName = null;
            serializedPath = null;

            if (!IsComponentPath(path))
            {
                return false;
            }

            var separator = path.IndexOf('/', Prefix.Length);
            if (separator < 0 || separator == path.Length - 1)
            {
                return false;
            }

            typeName = path.Substring(Prefix.Length, separator - Prefix.Length);
            serializedPath = path.Substring(separator + 1);
            return typeName.Length > 0 && serializedPath.Length > 0;
        }

        /// <summary>Finds the component on the object that owns <paramref name="typeName"/>.</summary>
        public static Component FindComponent(GameObject gameObject, string typeName)
        {
            if (gameObject == null || string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var components = gameObject.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    continue;
                }

                if (string.Equals(component.GetType().FullName, typeName, StringComparison.Ordinal))
                {
                    return component;
                }
            }

            return null;
        }

        /// <summary>Resolves a component type by full name across every loaded assembly.</summary>
        public static Type ResolveType(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                Type found;
                try
                {
                    found = assemblies[i].GetType(typeName, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (found != null && typeof(Component).IsAssignableFrom(found))
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>Human-readable field name for a status line.</summary>
        public static string ShortName(string path)
        {
            string typeName, serializedPath;
            if (!TryParse(path, out typeName, out serializedPath))
            {
                return path ?? "";
            }

            var dot = typeName.LastIndexOf('.');
            var shortType = dot >= 0 ? typeName.Substring(dot + 1) : typeName;

            var builder = new StringBuilder(shortType.Length + serializedPath.Length + 1);
            builder.Append(shortType).Append('.').Append(serializedPath);
            return builder.ToString();
        }
    }
}
