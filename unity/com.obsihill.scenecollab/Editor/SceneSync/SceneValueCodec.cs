using System;
using System.Globalization;
using UnityEditor;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Converts a <see cref="SerializedProperty"/> value to and from the string that travels over
    /// the wire.
    ///
    /// Culture-invariant on purpose: a float formatted with the machine's locale ("1,5" in de-DE)
    /// would deserialize as far-future garbage on a colleague's machine, and the bug would only
    /// appear between two people in different countries. Round-trip ("R") formatting keeps the
    /// value exactly reversible.
    ///
    /// Anything not listed here is refused rather than approximated — a property the codec cannot
    /// express must not be silently dropped, or the two editors drift apart with no error.
    /// </summary>
    public static class SceneValueCodec
    {
        /// <summary>Arrays above this size are left alone; a terrain layer is not a property edit.</summary>
        public const int MaxArrayElements = 64;

        public static bool TryRead(SerializedProperty property, out string value)
        {
            value = null;
            if (property == null)
            {
                return false;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Vector3:
                    value = FormatVector(property.vector3Value);
                    return true;

                case SerializedPropertyType.Quaternion:
                    var q = property.quaternionValue;
                    value = Format(q.x) + "," + Format(q.y) + "," + Format(q.z) + "," + Format(q.w);
                    return true;

                case SerializedPropertyType.Vector2:
                    var v2 = property.vector2Value;
                    value = Format(v2.x) + "," + Format(v2.y);
                    return true;

                case SerializedPropertyType.Boolean:
                    value = property.boolValue ? "true" : "false";
                    return true;

                case SerializedPropertyType.String:
                    value = property.stringValue ?? "";
                    return true;

                case SerializedPropertyType.Float:
                    value = Format(property.floatValue);
                    return true;

                case SerializedPropertyType.Integer:
                    value = property.intValue.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Color:
                    var c = property.colorValue;
                    value = Format(c.r) + "," + Format(c.g) + "," + Format(c.b) + "," + Format(c.a);
                    return true;

                case SerializedPropertyType.Enum:
                    value = property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.LayerMask:
                    value = property.intValue.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Vector4:
                    var v4 = property.vector4Value;
                    value = Format(v4.x) + "," + Format(v4.y) + "," + Format(v4.z) + "," + Format(v4.w);
                    return true;

                case SerializedPropertyType.Vector2Int:
                    var v2i = property.vector2IntValue;
                    value = v2i.x.ToString(CultureInfo.InvariantCulture) + "," +
                            v2i.y.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Vector3Int:
                    var v3i = property.vector3IntValue;
                    value = v3i.x.ToString(CultureInfo.InvariantCulture) + "," +
                            v3i.y.ToString(CultureInfo.InvariantCulture) + "," +
                            v3i.z.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Rect:
                    var r = property.rectValue;
                    value = Format(r.x) + "," + Format(r.y) + "," + Format(r.width) + "," + Format(r.height);
                    return true;

                case SerializedPropertyType.RectInt:
                    var ri = property.rectIntValue;
                    value = ri.x.ToString(CultureInfo.InvariantCulture) + "," +
                            ri.y.ToString(CultureInfo.InvariantCulture) + "," +
                            ri.width.ToString(CultureInfo.InvariantCulture) + "," +
                            ri.height.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Bounds:
                    var b = property.boundsValue;
                    value = FormatVector(b.center) + "," + FormatVector(b.extents);
                    return true;

                case SerializedPropertyType.BoundsInt:
                    var bi = property.boundsIntValue;
                    value = bi.position.x.ToString(CultureInfo.InvariantCulture) + "," +
                            bi.position.y.ToString(CultureInfo.InvariantCulture) + "," +
                            bi.position.z.ToString(CultureInfo.InvariantCulture) + "," +
                            bi.size.x.ToString(CultureInfo.InvariantCulture) + "," +
                            bi.size.y.ToString(CultureInfo.InvariantCulture) + "," +
                            bi.size.z.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Generic:
                    return property.isArray && TryReadArray(property, out value);

                default:
                    return false;
            }
        }

        public static bool TryWrite(SerializedProperty property, string value)
        {
            if (property == null || value == null)
            {
                return false;
            }

            float[] numbers;

            switch (property.propertyType)
            {
                case SerializedPropertyType.Vector3:
                    if (!TryParseNumbers(value, 3, out numbers))
                    {
                        return false;
                    }

                    property.vector3Value = new UnityEngine.Vector3(numbers[0], numbers[1], numbers[2]);
                    return true;

                case SerializedPropertyType.Quaternion:
                    if (!TryParseNumbers(value, 4, out numbers))
                    {
                        return false;
                    }

                    property.quaternionValue =
                        new UnityEngine.Quaternion(numbers[0], numbers[1], numbers[2], numbers[3]);
                    return true;

                case SerializedPropertyType.Vector2:
                    if (!TryParseNumbers(value, 2, out numbers))
                    {
                        return false;
                    }

                    property.vector2Value = new UnityEngine.Vector2(numbers[0], numbers[1]);
                    return true;

                case SerializedPropertyType.Boolean:
                    bool flag;
                    if (!bool.TryParse(value, out flag))
                    {
                        return false;
                    }

                    property.boolValue = flag;
                    return true;

                case SerializedPropertyType.String:
                    property.stringValue = value;
                    return true;

                case SerializedPropertyType.Float:
                    float single;
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out single))
                    {
                        return false;
                    }

                    property.floatValue = single;
                    return true;

                case SerializedPropertyType.Integer:
                    int integer;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                    {
                        return false;
                    }

                    property.intValue = integer;
                    return true;

                case SerializedPropertyType.Color:
                    if (!TryParseNumbers(value, 4, out numbers))
                    {
                        return false;
                    }

                    property.colorValue = new UnityEngine.Color(numbers[0], numbers[1], numbers[2], numbers[3]);
                    return true;

                case SerializedPropertyType.Enum:
                    int index;
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
                    {
                        return false;
                    }

                    property.enumValueIndex = index;
                    return true;

                case SerializedPropertyType.LayerMask:
                    if (!TryParseInts(value, 1, out var mask))
                    {
                        return false;
                    }

                    property.intValue = mask[0];
                    return true;

                case SerializedPropertyType.Vector4:
                    if (!TryParseNumbers(value, 4, out numbers))
                    {
                        return false;
                    }

                    property.vector4Value = new UnityEngine.Vector4(numbers[0], numbers[1], numbers[2], numbers[3]);
                    return true;

                case SerializedPropertyType.Vector2Int:
                    if (!TryParseInts(value, 2, out var v2i))
                    {
                        return false;
                    }

                    property.vector2IntValue = new UnityEngine.Vector2Int(v2i[0], v2i[1]);
                    return true;

                case SerializedPropertyType.Vector3Int:
                    if (!TryParseInts(value, 3, out var v3i))
                    {
                        return false;
                    }

                    property.vector3IntValue = new UnityEngine.Vector3Int(v3i[0], v3i[1], v3i[2]);
                    return true;

                case SerializedPropertyType.Rect:
                    if (!TryParseNumbers(value, 4, out var rect))
                    {
                        return false;
                    }

                    property.rectValue = new UnityEngine.Rect(rect[0], rect[1], rect[2], rect[3]);
                    return true;

                case SerializedPropertyType.RectInt:
                    if (!TryParseInts(value, 4, out var rectInt))
                    {
                        return false;
                    }

                    property.rectIntValue = new UnityEngine.RectInt(rectInt[0], rectInt[1], rectInt[2], rectInt[3]);
                    return true;

                case SerializedPropertyType.Bounds:
                    if (!TryParseNumbers(value, 6, out var bounds))
                    {
                        return false;
                    }

                    property.boundsValue = new UnityEngine.Bounds(
                        new UnityEngine.Vector3(bounds[0], bounds[1], bounds[2]),
                        new UnityEngine.Vector3(bounds[3], bounds[4], bounds[5]) * 2f);
                    return true;

                case SerializedPropertyType.BoundsInt:
                    if (!TryParseInts(value, 6, out var boundsInt))
                    {
                        return false;
                    }

                    property.boundsIntValue = new UnityEngine.BoundsInt(
                        new UnityEngine.Vector3Int(boundsInt[0], boundsInt[1], boundsInt[2]),
                        new UnityEngine.Vector3Int(boundsInt[3], boundsInt[4], boundsInt[5]));
                    return true;

                case SerializedPropertyType.Generic:
                    return property.isArray && TryWriteArray(property, value);

                default:
                    return false;
            }
        }

        /// <summary>
        /// Small arrays of scalars travel as one value: a count, then the elements.
        /// Index-addressed element paths are not stable between editors, and a terrain-sized array is
        /// megabytes of noise, so anything past <see cref="MaxArrayElements"/> stays unsynced.
        /// </summary>
        static bool TryReadArray(SerializedProperty property, out string value)
        {
            value = null;
            var count = property.arraySize;
            if (count > MaxArrayElements)
            {
                return false;
            }

            var parts = new System.Text.StringBuilder();
            parts.Append(count.ToString(CultureInfo.InvariantCulture));
            for (var i = 0; i < count; i++)
            {
                var element = property.GetArrayElementAtIndex(i);
                if (!TryReadScalar(element, out var elementValue))
                {
                    return false;
                }

                parts.Append(';').Append(EscapeElement(elementValue));
            }

            value = parts.ToString();
            return true;
        }

        static bool TryWriteArray(SerializedProperty property, string value)
        {
            var chunks = value.Split(';');
            if (!int.TryParse(chunks[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count < 0)
            {
                return false;
            }

            if (count > MaxArrayElements || chunks.Length - 1 != count)
            {
                return false;
            }

            property.arraySize = count;
            for (var i = 0; i < count; i++)
            {
                var element = property.GetArrayElementAtIndex(i);
                if (!TryWriteScalar(element, UnescapeElement(chunks[i + 1])))
                {
                    return false;
                }
            }

            return true;
        }

        static bool TryReadScalar(SerializedProperty property, out string value)
        {
            value = null;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    value = Format(property.floatValue);
                    return true;

                case SerializedPropertyType.Integer:
                    value = property.intValue.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Boolean:
                    value = property.boolValue ? "true" : "false";
                    return true;

                case SerializedPropertyType.String:
                    value = property.stringValue ?? "";
                    return true;

                case SerializedPropertyType.Enum:
                    value = property.enumValueIndex.ToString(CultureInfo.InvariantCulture);
                    return true;

                case SerializedPropertyType.Color:
                    var c = property.colorValue;
                    value = Format(c.r) + "," + Format(c.g) + "," + Format(c.b) + "," + Format(c.a);
                    return true;

                case SerializedPropertyType.Vector2:
                    var v2 = property.vector2Value;
                    value = Format(v2.x) + "," + Format(v2.y);
                    return true;

                case SerializedPropertyType.Vector3:
                    var v3 = property.vector3Value;
                    value = Format(v3.x) + "," + Format(v3.y) + "," + Format(v3.z);
                    return true;

                case SerializedPropertyType.Vector4:
                    var v4 = property.vector4Value;
                    value = Format(v4.x) + "," + Format(v4.y) + "," + Format(v4.z) + "," + Format(v4.w);
                    return true;

                default:
                    return false;
            }
        }

        static bool TryWriteScalar(SerializedProperty property, string value)
        {
            if (property == null || value == null)
            {
                return false;
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Float:
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    {
                        return false;
                    }

                    property.floatValue = f;
                    return true;

                case SerializedPropertyType.Integer:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    {
                        return false;
                    }

                    property.intValue = i;
                    return true;

                case SerializedPropertyType.Boolean:
                    if (value != "true" && value != "false")
                    {
                        return false;
                    }

                    property.boolValue = value == "true";
                    return true;

                case SerializedPropertyType.String:
                    property.stringValue = value;
                    return true;

                case SerializedPropertyType.Enum:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                    {
                        return false;
                    }

                    property.enumValueIndex = index;
                    return true;

                case SerializedPropertyType.Color:
                    if (!TryParseNumbers(value, 4, out var color))
                    {
                        return false;
                    }

                    property.colorValue = new UnityEngine.Color(color[0], color[1], color[2], color[3]);
                    return true;

                case SerializedPropertyType.Vector2:
                    if (!TryParseNumbers(value, 2, out var v2))
                    {
                        return false;
                    }

                    property.vector2Value = new UnityEngine.Vector2(v2[0], v2[1]);
                    return true;

                case SerializedPropertyType.Vector3:
                    if (!TryParseNumbers(value, 3, out var v3))
                    {
                        return false;
                    }

                    property.vector3Value = new UnityEngine.Vector3(v3[0], v3[1], v3[2]);
                    return true;

                case SerializedPropertyType.Vector4:
                    if (!TryParseNumbers(value, 4, out var v4))
                    {
                        return false;
                    }

                    property.vector4Value = new UnityEngine.Vector4(v4[0], v4[1], v4[2], v4[3]);
                    return true;

                default:
                    return false;
            }
        }

        static string EscapeElement(string value)
        {
            return value.Replace("%", "%25").Replace(";", "%3B");
        }

        static string UnescapeElement(string value)
        {
            return value.Replace("%3B", ";").Replace("%25", "%");
        }

        static bool TryParseInts(string value, int expected, out int[] numbers)
        {
            numbers = null;
            var parts = value.Split(',');
            if (parts.Length != expected)
            {
                return false;
            }

            var parsed = new int[expected];
            for (var i = 0; i < expected; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed[i]))
                {
                    return false;
                }
            }

            numbers = parsed;
            return true;
        }

        /// <summary>True when the two strings describe the same value; used to suppress echo ops.</summary>
        public static bool AreEqual(string left, string right)
        {
            return string.Equals(left ?? "", right ?? "", StringComparison.Ordinal);
        }

        static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        static string FormatVector(UnityEngine.Vector3 vector)
        {
            return Format(vector.x) + "," + Format(vector.y) + "," + Format(vector.z);
        }

        static bool TryParseNumbers(string value, int expected, out float[] numbers)
        {
            numbers = null;
            var parts = value.Split(',');
            if (parts.Length != expected)
            {
                return false;
            }

            var parsed = new float[expected];
            for (var i = 0; i < expected; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                {
                    return false;
                }
            }

            numbers = parsed;
            return true;
        }
    }
}
