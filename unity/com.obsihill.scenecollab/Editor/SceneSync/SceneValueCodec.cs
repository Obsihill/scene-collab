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

                default:
                    return false;
            }
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
