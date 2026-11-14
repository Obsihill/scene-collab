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
    /// A component field is addressed as <c>c/&lt;TypeFullName&gt;#&lt;slot&gt;/&lt;serialized path&gt;</c>.
    /// Type plus slot, never "the third component": two editors can have their components in a
    /// different order, but they agree on which AudioSource is the second AudioSource. Without the slot
    /// a second component of the same type is unreachable — every field op lands on the first one.
    ///
    /// Values the codec can express exactly are tracked — numbers, bools, strings, enums, colours,
    /// vectors — plus object references, which travel as an asset guid + local file id or as a
    /// scene-internal node id. A reference that cannot be resolved on the receiving machine is not
    /// dropped silently: it becomes a visible ERROR stand-in.
    /// </summary>
    public static class SceneComponentFields
    {
        public const string Prefix = "c/";

        /// <summary>Separates the type name from the instance slot: <c>c/UnityEngine.AudioSource#1/m_Volume</c>.</summary>
        public const char SlotSeparator = '#';

        /// <summary>Builds the wire path for one field of one component instance.</summary>
        public static string BuildPath(string typeName, int slot, string serializedPath)
        {
            return Prefix + typeName + SlotSeparator + slot.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "/" + serializedPath;
        }

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
            "m_ObjectHideFlags", // Local editor guards must never become shared scene state.
            "m_CorrespondingSourceObject",
            "m_PrefabInstance",
            "m_PrefabAsset",
        };

        /// <summary>Paths for every trackable field of an object, one path per component instance.</summary>
        public static void CollectPaths(GameObject gameObject, List<string> into)
        {
            if (gameObject == null)
            {
                return;
            }

            var components = gameObject.GetComponents<Component>();
            var slots = new Dictionary<string, int>(StringComparer.Ordinal);

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

                // Slot counter per type. Unity's own enumeration order is the instance order, so both
                // editors number the second AudioSource as 1 even when the objects differ elsewhere.
                int slot;
                slots.TryGetValue(typeName, out slot);
                slots[typeName] = slot + 1;

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

                        into.Add(BuildPath(typeName, slot, path));
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

                        into.Add(BuildPath(typeName, slot, path));
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
                // A reference is an identity, not a value: it travels as an asset guid or a node id and
                // is resolved on the receiver. Anything unresolvable becomes an ERROR stand-in.
                case SerializedPropertyType.ObjectReference:
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>Value kinds an array may hold. References are excluded: an array of PPtrs is a
        /// residency table, not a property an editor edits one field at a time.</summary>
        public static bool IsScalar(SerializedPropertyType type)
        {
            return type != SerializedPropertyType.ObjectReference && IsSyncable(type);
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

            return IsScalar(property.GetArrayElementAtIndex(0).propertyType);
        }

        public static bool IsComponentPath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.StartsWith(Prefix, StringComparison.Ordinal);
        }

        /// <summary>Splits <c>c/Type#slot/field</c> into its three parts. A path without a slot is slot 0.</summary>
        public static bool TryParse(string path, out string typeName, out int slot, out string serializedPath)
        {
            typeName = null;
            slot = 0;
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

            var head = path.Substring(Prefix.Length, separator - Prefix.Length);
            serializedPath = path.Substring(separator + 1);

            var hash = head.LastIndexOf(SlotSeparator);
            if (hash >= 0)
            {
                int parsed;
                if (!int.TryParse(head.Substring(hash + 1), System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out parsed) || parsed < 0)
                {
                    return false;
                }

                slot = parsed;
                head = head.Substring(0, hash);
            }

            typeName = head;
            return typeName.Length > 0 && serializedPath.Length > 0;
        }

        /// <summary>Finds the component in slot <paramref name="slot"/> of the type on the object.</summary>
        public static Component FindComponent(GameObject gameObject, string typeName, int slot)
        {
            if (gameObject == null || string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var components = gameObject.GetComponents<Component>();
            var seen = 0;
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    continue;
                }

                if (!string.Equals(component.GetType().FullName, typeName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen == slot)
                {
                    return component;
                }

                seen++;
            }

            return null;
        }

        /// <summary>How many components of this type the object has; the next free slot for an add.</summary>
        public static int CountOfType(GameObject gameObject, string typeName)
        {
            if (gameObject == null || string.IsNullOrEmpty(typeName))
            {
                return 0;
            }

            var components = gameObject.GetComponents<Component>();
            var count = 0;
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component != null
                    && string.Equals(component.GetType().FullName, typeName, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
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
            int slot;
            if (!TryParse(path, out typeName, out slot, out serializedPath))
            {
                return path ?? "";
            }

            var dot = typeName.LastIndexOf('.');
            var shortType = dot >= 0 ? typeName.Substring(dot + 1) : typeName;

            var builder = new StringBuilder(shortType.Length + serializedPath.Length + 6);
            builder.Append(shortType);
            if (slot > 0)
            {
                builder.Append(SlotSeparator).Append(slot);
            }

            builder.Append('.').Append(serializedPath);
            return builder.ToString();
        }
    }
}
