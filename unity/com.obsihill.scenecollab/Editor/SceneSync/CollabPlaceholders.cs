using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Stand-ins for content this editor does not have — deliberately loud, GMod-style.
    ///
    /// Unity's own failure modes are quiet ones: a missing mesh renders as nothing, a missing material as
    /// magenta, a missing script as "Missing (Mono Script)" that cannot be inspected. A collaborator then
    /// stares at an empty patch of level and cannot tell whether the artist deleted something or whether
    /// their checkout is incomplete.
    ///
    /// So a missing thing does not render as nothing here: it renders as a glowing magenta ERROR cube with
    /// a floating label, slowly turning. That is a deliberate joke at our own expense, and it is also the
    /// fastest way to answer "why does my level look different from theirs" — you can see it from across
    /// the scene view instead of hunting the inspector.
    ///
    /// Everything created here is <see cref="HideFlags.DontSave"/> and is never registered as a node, so it
    /// cannot leak into the host's saved scene or into the op stream.
    /// </summary>
    public static class CollabPlaceholders
    {
        public const string MarkerName = "__collab_missing__";
        public const string MarkerTag = "value:missing";

        /// <summary>Magenta, unlit-ish and emissive: visible in a dark scene and in any render pipeline.</summary>
        static readonly Color ErrorColor = new Color(1f, 0.05f, 0.75f, 1f);

        static Material _material;
        static Material _labelMaterial;
        static Font _font;

        /// <summary>Every live marker, so the editor update can animate them without a scene scan.</summary>
        static readonly List<CollabMissingMarker> Markers = new List<CollabMissingMarker>();

        [InitializeOnLoadMethod]
        static void Hook()
        {
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

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
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
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
            return AttachError(target, label);
        }

        /// <summary>
        /// The whole-object stand-in: a spinning emissive cube sized to the object's own bounds, with the
        /// reason floating above it. Used when nothing of the object could be built (missing prefab, missing
        /// script) rather than when one field of an otherwise fine object could not be resolved.
        /// </summary>
        public static GameObject AttachError(GameObject target, string label)
        {
            return AttachError(target, label, null, true);
        }

        /// <summary>
        /// A single field's stand-in: a small cube on the object, tagged with the field path so a later
        /// successful write can remove exactly this one and leave the others alone.
        /// </summary>
        public static GameObject AttachError(GameObject target, string label, string fieldPath)
        {
            return AttachError(target, label, fieldPath, false);
        }

        static GameObject AttachError(GameObject target, string label, string fieldPath, bool wholeObject)
        {
            if (target == null)
            {
                return null;
            }

            // Field-level errors replace their previous marker instead of stacking: a reconnecting peer
            // re-applies the same snapshot, and forty cubes on one object help nobody.
            if (!string.IsNullOrEmpty(fieldPath))
            {
                ClearError(target, fieldPath);
            }

            var box = Create(label, Vector3.one);
            if (box == null)
            {
                return null;
            }

            box.transform.SetParent(target.transform, false);
            box.transform.localPosition = wholeObject ? Vector3.zero : Vector3.up * (ObjectSize(target).y * 0.5f + 0.2f);
            box.transform.localRotation = Quaternion.identity;
            box.transform.localScale = Vector3.one * (wholeObject ? 1f : 0.5f);
            box.gameObject.SetActive(true);

            var marker = box.AddComponent<CollabMissingMarker>();
            marker.label = label;
            marker.fieldPath = fieldPath;
            marker.baseScale = Vector3.one * (wholeObject ? 1f : 0.5f);
            marker.hideFlags = HideFlags.DontSave;

            AddLabel(box.transform, fieldPath == null ? "ERROR" : "ERROR\n" + SceneComponentFields.ShortName(fieldPath), wholeObject);

            if (!Markers.Contains(marker))
            {
                Markers.Add(marker);
            }

            return box;
        }

        /// <summary>Removes the placeholder that belongs to one field, if it is still there.</summary>
        public static bool ClearError(GameObject target, string fieldPath)
        {
            if (target == null || string.IsNullOrEmpty(fieldPath))
            {
                return false;
            }

            var markers = target.GetComponentsInChildren<CollabMissingMarker>(true);
            var cleared = false;
            for (var i = 0; i < markers.Length; i++)
            {
                var marker = markers[i];
                if (marker == null || !string.Equals(marker.fieldPath, fieldPath, StringComparison.Ordinal))
                {
                    continue;
                }

                Markers.Remove(marker);
                UnityEngine.Object.DestroyImmediate(marker.gameObject);
                cleared = true;
            }

            return cleared;
        }

        /// <summary>True when this object is (or carries) a placeholder rather than real content.</summary>
        public static bool IsPlaceholder(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return false;
            }

            return gameObject.name.StartsWith(MarkerName, StringComparison.Ordinal)
                || gameObject.GetComponent<CollabMissingMarker>() != null;
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
                    Markers.Remove(markers[i]);
                    UnityEngine.Object.DestroyImmediate(markers[i].gameObject);
                }
            }

            return markers.Length;
        }

        /// <summary>Spins and bobs every live marker. Cheap: a handful of transforms at most.</summary>
        static void Tick()
        {
            if (Markers.Count == 0)
            {
                return;
            }

            var now = (float)EditorApplication.timeSinceStartup;

            for (var i = Markers.Count - 1; i >= 0; i--)
            {
                var marker = Markers[i];
                if (marker == null)
                {
                    Markers.RemoveAt(i);
                    continue;
                }

                if (EditorApplication.isPlayingOrWillChangePlaymode)
                {
                    continue;
                }

                marker.Tick(now);
            }
        }

        static Vector3 ObjectSize(GameObject target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return Vector3.one;
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            if (bounds.size.sqrMagnitude < 1e-6f)
            {
                return Vector3.one;
            }

            return bounds.size;
        }

        static void AddLabel(Transform parent, string text, bool big)
        {
            var label = new GameObject("label");
            label.hideFlags = HideFlags.DontSave;
            label.transform.SetParent(parent, false);
            label.transform.localPosition = Vector3.up * (big ? 30f : 220f);
            label.transform.localRotation = Quaternion.identity;
            label.transform.localScale = Vector3.one;

            var mesh = label.AddComponent<TextMesh>();
            mesh.text = text;
            mesh.anchor = TextAnchor.MiddleCenter;
            mesh.alignment = TextAlignment.Center;
            mesh.fontSize = 64;
            mesh.characterSize = big ? 30f : 90f;
            mesh.color = Color.white;

            var font = Font();
            if (font != null)
            {
                mesh.font = font;
                var renderer = label.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterial = LabelMaterial();
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    renderer.receiveShadows = false;
                }
            }
        }

        static Font Font()
        {
            if (_font != null)
            {
                return _font;
            }

            // Arial.ttf was removed from the builtin resources in 2022.2; LegacyRuntime.ttf replaced it.
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (_font == null)
            {
                _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }

            return _font;
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
            _material.name = "SceneCollab Error (emissive)";
            _material.hideFlags = HideFlags.DontSave;
            _material.color = ErrorColor;

            if (_material.HasProperty("_BaseColor"))
            {
                _material.SetColor("_BaseColor", ErrorColor);
            }

            if (_material.HasProperty("_Color"))
            {
                _material.SetColor("_Color", ErrorColor);
            }

            // Emission is the whole point: an ERROR cube has to glow in a dark level, and a plain magenta
            // cube at dusk reads as "purple prop" instead of "something is missing".
            if (_material.HasProperty("_EmissionColor"))
            {
                _material.EnableKeyword("_EMISSION");
                _material.SetColor("_EmissionColor", ErrorColor * 3f);

                // Realtime GI would bake the placeholder into a lightmap and outlive the session.
                _material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }

            return _material;
        }

        static Material LabelMaterial()
        {
            if (_labelMaterial != null)
            {
                return _labelMaterial;
            }

            var shader = Shader.Find("GUI/Text Shader");
            if (shader == null)
            {
                shader = Shader.Find("Unlit/Transparent");
            }

            _labelMaterial = new Material(shader);
            _labelMaterial.name = "SceneCollab Error Label";
            _labelMaterial.hideFlags = HideFlags.DontSave;
            return _labelMaterial;
        }

        /// <summary>Counts missing assets on the loaded scene's objects and builds the operator report.</summary>
        public static string Describe(int missingAssets, int missingScripts, int missingReferences)
        {
            var parts = new List<string>(3);
            if (missingAssets > 0)
            {
                parts.Add(missingAssets + " missing asset(s)");
            }

            if (missingScripts > 0)
            {
                parts.Add(missingScripts + " missing script(s)");
            }

            if (missingReferences > 0)
            {
                parts.Add(missingReferences + " unresolved reference(s)");
            }

            if (parts.Count == 0)
            {
                return null;
            }

            return string.Join(", ", parts.ToArray()) + " — ERROR markers shown";
        }

        public static string Describe(int missingAssets, int missingScripts)
        {
            return Describe(missingAssets, missingScripts, 0);
        }
    }

    /// <summary>Marks a placeholder so it can be found and reverted later.</summary>
    [ExecuteAlways]
    public sealed class CollabMissingMarker : MonoBehaviour
    {
        public string label;

        /// <summary>Set for field-level markers; null for whole-object ones.</summary>
        public string fieldPath;

        public Vector3 baseScale = Vector3.one;

        Vector3 _basePosition;
        bool _captured;

        public void Tick(float now)
        {
            if (this == null || transform == null)
            {
                return;
            }

            if (!_captured)
            {
                _basePosition = transform.localPosition;
                _captured = true;
            }

            // A slow turn and a small bob: enough motion that the eye catches it in a still scene, small
            // enough that it never fights the object you are actually trying to edit.
            transform.localRotation = Quaternion.Euler(0f, (now * 45f) % 360f, 0f);
            transform.localPosition = _basePosition + Vector3.up * (Mathf.Sin(now * 2f) * 0.06f);
            transform.localScale = baseScale * (1f + Mathf.Sin(now * 3f) * 0.03f);

            var labelTransform = transform.childCount > 0 ? transform.GetChild(0) : null;
            var camera = Camera.current != null ? Camera.current : SceneView.lastActiveSceneView?.camera;
            if (labelTransform != null && camera != null)
            {
                // Billboard, not parent-relative: text that turns away with the cube is unreadable at
                // exactly the angles where you notice an object is wrong.
                labelTransform.rotation = camera.transform.rotation;
                labelTransform.localScale = Vector3.one;
            }
        }

        void OnDrawGizmos()
        {
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireCube(transform.position, transform.lossyScale);
        }
    }
}
