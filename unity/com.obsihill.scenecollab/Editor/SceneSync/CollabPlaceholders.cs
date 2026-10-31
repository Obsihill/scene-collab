using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Stand-ins for content this editor does not have.
    ///
    /// Unity's own failure modes are quiet ones: a missing mesh renders as nothing, a missing material as
    /// magenta, a missing script as "Missing (Mono Script)" that cannot be inspected. A collaborator then
    /// stares at an empty patch of level and cannot tell whether the artist deleted something or whether
    /// their checkout is incomplete. The placeholder is the loud version of the same information.
    ///
    /// Everything created here is <see cref="HideFlags.DontSave"/> and is never registered as a node, so it
    /// cannot leak into the host's saved scene or into the op stream.
    /// </summary>
    public static class CollabPlaceholders
    {
        public const string MarkerName = "__collab_missing__";
        public const string MarkerTag = "value:missing";

        static Material _material;
        static Mesh _mesh;

        /// <summary>Builds the magenta box used for missing meshes and prefabs.</summary>
        public static GameObject Create(string label, Vector3 size)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (box == null)
            {
                return null;
            }

            box.name = MarkerName + " " + label;
            box.hideFlags = HideFlags.DontSave;

            var renderer = box.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = ErrorMaterial();
                renderer.hideFlags = HideFlags.DontSave;
            }

            var collider = box.GetComponent<Collider>();
            if (collider != null)
            {
                // No physics in a placeholder: a stray collider would block the host's raycasts.
                UnityEngine.Object.DestroyImmediate(collider);
            }

            box.transform.localScale = size;
            return box;
        }

        /// <summary>Adds a placeholder child under <paramref name="target"/> describing what is missing.</summary>
        public static GameObject Attach(GameObject target, string label)
        {
            if (target == null)
            {
                return null;
            }

            var box = Create(label, Vector3.one);
            if (box == null)
            {
                return null;
            }

            box.transform.SetParent(target.transform, false);
            box.transform.localPosition = Vector3.zero;
            box.transform.localRotation = Quaternion.identity;
            box.transform.localScale = Vector3.one * 0.5f;

            var marker = box.AddComponent<CollabMissingMarker>();
            marker.label = label;
            marker.hideFlags = HideFlags.DontSave;

            return box;
        }

        /// <summary>Removes every placeholder under <paramref name="root"/> (used when the asset arrives).</summary>
        public static int Clear(GameObject root)
        {
            if (root == null)
            {
                return 0;
            }

            var markers = root.GetComponentsInChildren<CollabMissingMarker>(true);
            for (var i = 0; i < markers.Length; i++)
            {
                if (markers[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(markers[i].gameObject);
                }
            }

            return markers.Length;
        }

        static Material ErrorMaterial()
        {
            if (_material != null)
            {
                return _material;
            }

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                shader = Shader.Find("Unlit/Color");
            }

            _material = new Material(shader);
            _material.name = "SceneCollab Missing";
            _material.hideFlags = HideFlags.DontSave;
            _material.color = new Color(1f, 0f, 1f, 1f);
            return _material;
        }

        /// <summary>Counts missing assets on the loaded scene's objects and builds the operator report.</summary>
        public static string Describe(int missingAssets, int missingScripts)
        {
            if (missingAssets <= 0 && missingScripts <= 0)
            {
                return null;
            }

            var parts = new List<string>(2);
            if (missingAssets > 0)
            {
                parts.Add(missingAssets + " missing asset(s)");
            }

            if (missingScripts > 0)
            {
                parts.Add(missingScripts + " missing script(s)");
            }

            return string.Join(", ", parts.ToArray()) + " — placeholders shown";
        }
    }

    /// <summary>Marks a placeholder so it can be found and reverted later.</summary>
    public sealed class CollabMissingMarker : MonoBehaviour
    {
        public string label;

        void OnDrawGizmos()
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireCube(transform.position, transform.lossyScale);
        }
    }
}
