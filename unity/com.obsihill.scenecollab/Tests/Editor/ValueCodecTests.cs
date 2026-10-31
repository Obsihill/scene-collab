using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// The codec is the only place a value crosses the wire. Anything it cannot express must be
    /// refused loudly (TryRead/TryWrite false), never rounded or dropped: a silently skipped field is
    /// two editors drifting apart with no error to show for it.
    /// </summary>
    public class ValueCodecTests
    {
        ValueCodecProbeAsset _probe;
        SerializedObject _serialized;

        [SetUp]
        public void SetUp()
        {
            _probe = ScriptableObject.CreateInstance<ValueCodecProbeAsset>();
            _serialized = new SerializedObject(_probe);
        }

        [TearDown]
        public void TearDown()
        {
            if (_probe != null)
            {
                Object.DestroyImmediate(_probe);
            }
        }

        [Test]
        public void StructTypesRoundTrip()
        {
            Assert.AreEqual("1.5,2.5,3.5,4.5", RoundTrip("v4", "1.5,2.5,3.5,4.5"));
            Assert.AreEqual("7,8", RoundTrip("v2i", "7,8"));
            Assert.AreEqual("-1,2,-3", RoundTrip("v3i", "-1,2,-3"));
            Assert.AreEqual("1,2,30,40", RoundTrip("rect", "1,2,30,40"));
            Assert.AreEqual("5,6,7,8", RoundTrip("rectInt", "5,6,7,8"));
            Assert.AreEqual("1,2,3,4,5,6", RoundTrip("bounds", "1,2,3,4,5,6"));
            Assert.AreEqual("9,8,7,6,5,4", RoundTrip("boundsInt", "9,8,7,6,5,4"));
            Assert.AreEqual("3", RoundTrip("mask", "3"));
        }

        [Test]
        public void BoundsTravelsAsCenterAndExtents()
        {
            RoundTrip("bounds", "1,2,3,4,5,6");

            Assert.AreEqual(new Vector3(1f, 2f, 3f), _probe.bounds.center);
            Assert.AreEqual(new Vector3(8f, 10f, 12f), _probe.bounds.size);
        }

        [Test]
        public void StructFieldsRejectTheWrongArity()
        {
            var property = Property("v4");
            Assert.IsFalse(SceneValueCodec.TryWrite(property, "1,2,3"));
            Assert.IsFalse(SceneValueCodec.TryWrite(property, "1,2,3,4,5"));
            Assert.IsFalse(SceneValueCodec.TryWrite(property, "a,b,c,d"));
        }

        [Test]
        public void SmallScalarArraysTravelAsOneValue()
        {
            Assert.AreEqual("2;1;2", RoundTrip("floats", "2;1;2"));
            Assert.AreEqual("2;1.5,2.5,3.5;4,5,6", RoundTrip("vectors", "2;1.5,2.5,3.5;4,5,6"));

            // A separator inside a string element is escaped, not mistaken for the next element.
            Assert.AreEqual("2;a%3Bb;c%25d", RoundTrip("strings", "2;a%3Bb;c%25d"));
            Assert.AreEqual("a;b", _probe.strings[0]);
            Assert.AreEqual("c%d", _probe.strings[1]);
        }

        [Test]
        public void ArrayWriteResizesTheArray()
        {
            Assert.AreEqual(2, _probe.floats.Length);
            RoundTrip("floats", "4;9;8;7;6");
            Assert.AreEqual(new[] { 9f, 8f, 7f, 6f }, _probe.floats);
        }

        [Test]
        public void MalformedArrayIsRefusedAndLeavesTheArrayAlone()
        {
            var property = Property("floats");
            Assert.IsFalse(SceneValueCodec.TryWrite(property, "2;1"));
            Assert.IsFalse(SceneValueCodec.TryWrite(property, "x;1;2"));
            Assert.IsFalse(SceneValueCodec.TryWrite(property, "-1;"));

            _serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.AreEqual(new[] { 1f, 2f }, _probe.floats);
        }

        [Test]
        public void OversizedArrayIsNotSyncable()
        {
            Assert.AreEqual(65, _probe.wide.Length);
            Assert.IsFalse(SceneComponentFields.IsSyncableArray(Property("wide")));
            Assert.IsFalse(SceneValueCodec.TryRead(Property("wide"), out _));
        }

        [Test]
        public void EmptyArrayIsNotSyncable()
        {
            Assert.IsFalse(SceneComponentFields.IsSyncableArray(Property("empty")));
        }

        [Test]
        public void EveryDeclaredFieldTypeIsSyncable()
        {
            var names = new[]
            {
                "v4", "v2i", "v3i", "rect", "rectInt", "bounds", "boundsInt", "mask",
            };

            foreach (var name in names)
            {
                var property = Property(name);
                Assert.IsTrue(SceneComponentFields.IsSyncable(property.propertyType),
                    name + " (" + property.propertyType + ") is not syncable");
            }

            foreach (var name in new[] { "floats", "strings", "vectors" })
            {
                Assert.IsTrue(SceneComponentFields.IsSyncableArray(Property(name)), name + " array is not syncable");
            }
        }

        [Test]
        public void CollectorIncludesSmallArraysAndSkipsBigOnes()
        {
            var host = new GameObject("Collector");
            try
            {
                // No custom behaviour can be attached from an editor assembly, so the collector is
                // checked against a built-in component: a LineRenderer keeps Vector3 arrays.
                var line = host.AddComponent<LineRenderer>();
                line.positionCount = 3;
                line.SetPositions(new[] { Vector3.zero, Vector3.one, Vector3.up });

                var paths = new List<string>();
                SceneComponentFields.CollectPaths(host, paths);

                Assert.IsTrue(paths.Any(p => p.StartsWith(SceneComponentFields.Prefix, System.StringComparison.Ordinal)),
                    "no component fields collected: " + string.Join(", ", paths));

                // LineRenderer keeps its positions in a Vector3 array; the collector must hand it over
                // as one value, not element by element.
                Assert.IsTrue(paths.Any(p => p.EndsWith("/m_Positions", System.StringComparison.Ordinal)),
                    "the Vector3 array was not collected: " + string.Join(", ", paths));

                foreach (var path in paths)
                {
                    var parts = path.Substring(SceneComponentFields.Prefix.Length);
                    var slash = parts.IndexOf('/');
                    Assert.Greater(slash, 0, "malformed path: " + path);
                }
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        string RoundTrip(string field, string value)
        {
            var property = Property(field);
            Assert.IsTrue(SceneValueCodec.TryWrite(property, value), "write refused: " + field + " = " + value);
            _serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.IsTrue(SceneValueCodec.TryRead(_serialized.FindProperty(field), out var read), "read refused: " + field);
            return read;
        }

        SerializedProperty Property(string field)
        {
            var property = _serialized.FindProperty(field);
            Assert.IsNotNull(property, "probe has no field: " + field);
            return property;
        }
    }
}
