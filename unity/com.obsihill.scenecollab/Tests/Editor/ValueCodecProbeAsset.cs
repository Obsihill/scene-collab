using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// Test-only ScriptableObject whose fields cover every type the codec claims to express.
    ///
    /// A MonoBehaviour would be the obvious choice and is not: this assembly is editor-only, and Unity
    /// refuses <c>AddComponent</c> for a behaviour that lives in an editor assembly (it returns null),
    /// which reads as "the field was never collected" instead of "the probe could not be built".
    /// </summary>
    public sealed class ValueCodecProbeAsset : ScriptableObject
    {
        public Vector4 v4;
        public Vector2Int v2i;
        public Vector3Int v3i;
        public Rect rect;
        public RectInt rectInt;
        public Bounds bounds;
        public BoundsInt boundsInt;
        public LayerMask mask;

        public float[] floats = new[] { 1f, 2f };
        public string[] strings = new[] { "a;b", "c%d" };
        public Vector3[] vectors = new[] { Vector3.one, Vector3.up };
        public float[] wide = new float[65];
        public float[] empty = new float[0];
    }
}
