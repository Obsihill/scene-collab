using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

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
    /// So a missing thing does not render as nothing here: it renders as a bright stand-in sitting exactly
    /// where the missing object would have been. By default that is an emissive magenta cube; a project can
    /// point <see cref="CollabSettings.errorModelPath"/> at its own placeholder prefab (the Half-Life style
    /// ERROR sign, say) and that is used instead.
    ///
    /// Two rules the shape of this class follows:
    ///
    /// * **Never a child of the object it stands in for.** Parenting looked convenient and was wrong: a
    ///   parent with a non-uniform scale or a thin transform stretched and displaced the marker, so the
    ///   thing meant to say "here is what you are missing" pointed at the wrong place at the wrong size.
    ///   Markers are scene roots, sized in world units, and remember their owner for clearing.
    /// * **Nothing moves.** No spin, no bob, no camera-facing label: an annotation that tracks the view is
    ///   noise exactly while you are working. The reason lives in the marker's name in the hierarchy and in
    ///   the window's asset report.
    ///
    /// Everything created here is <see cref="HideFlags.DontSave"/> and is never registered as a node, so it
    /// cannot leak into the host's saved scene or into the op stream.
    /// </summary>
    public static class CollabPlaceholders
    {
        public const string MarkerName = "__collab_missing__";
        public const string MarkerTag = "value:missing";

        /// <summary>What made the marker: what the refresh pass re-checks to decide it can go away.</summary>
        public const string ReasonPrefab = "prefab";
        public const string ReasonScript = "script";
        public const string ReasonMesh = "mesh";
        public const string ReasonReference = "reference";

        /// <summary>Magenta, unlit-ish and emissive: visible in a dark scene and in any render pipeline.</summary>
        static readonly Color ErrorColor = new Color(1f, 0.05f, 0.75f, 1f);

        static Material _material;

        /// <summary>Every live marker, so a revert can drop them without scanning the scene.</summary>
        static readonly List<CollabMissingMarker> Markers = new List<CollabMissingMarker>();

        /// <summary>Builds the stand-in body used for missing meshes and prefabs.</summary>
        public static GameObject Create(string label, Vector3 size)
        {
            var body = BuildErrorModel(label);
            if (body == null)
            {
                body = BuildCube(label);
            }

            if (body == null)
            {
                return null;
            }

            body.name = MarkerName + " " + label;
            body.hideFlags = HideFlags.DontSave;

            var colliders = body.GetComponentsInChildren<Collider>(true);
            for (var i = 0; i < colliders.Length; i++)
            {
                // No physics in a placeholder: a stray collider would block the host's raycasts.
                UnityEngine.Object.DestroyImmediate(colliders[i]);
            }

            body.transform.localScale = size;
            return body;
        }

        /// <summary>Adds a stand-in for <paramref name="target"/> describing what is missing.</summary>
        public static GameObject Attach(GameObject target, string label)
        {
            return AttachError(target, label);
        }

        /// <summary>
        /// The whole-object stand-in, sized to the object's own bounds and placed on its centre. Used when
        /// nothing of the object could be built (missing prefab, missing script) rather than when one field
        /// of an otherwise fine object could not be resolved.
        /// </summary>
        public static GameObject AttachError(GameObject target, string label)
        {
            return AttachError(target, label, null, null, null, true);
        }

        /// <summary>
        /// A single field's stand-in: a small marker above the object, tagged with the field path so a later
        /// successful write can remove exactly this one and leave the others alone.
        /// </summary>
        public static GameObject AttachError(GameObject target, string label, string fieldPath)
        {
            return AttachError(target, label, fieldPath, null, ReasonReference);
        }

        /// <summary>Same, but remembering which asset the field pointed at, so the refresh can retire it.</summary>
        public static GameObject AttachError(GameObject target, string label, string fieldPath, string assetGuid)
        {
            return AttachError(target, label, fieldPath, assetGuid, 0L);
        }

        public static GameObject AttachError(GameObject target, string label, string fieldPath, string assetGuid,
            long localFileId)
        {
            var body = AttachError(target, label, fieldPath, assetGuid, ReasonReference);
            var component = body != null && target != null ? body.GetComponent<CollabMissingMarker>() : null;
            if (component != null)
            {
                component.localFileId = localFileId;
            }

            return body;
        }

        /// <summary>A node whose prefab asset this checkout does not have (yet).</summary>
        public static GameObject AttachPrefabError(GameObject target, string label, string assetGuid)
        {
            return AttachError(target, label, null, assetGuid, ReasonPrefab);
        }

        /// <summary>A component whose script type this project cannot load (yet).</summary>
        public static GameObject AttachScriptError(GameObject target, string label, string typeName)
        {
            var marker = AttachError(target, label, null, null, ReasonScript);
            var component = marker == null ? null : marker.GetComponent<CollabMissingMarker>();
            if (component != null)
            {
                component.scriptType = typeName;
            }

            return marker;
        }

        /// <summary>An object whose mesh or material is missing on this machine.</summary>
        public static GameObject AttachMeshError(GameObject target, string label)
        {
            return AttachError(target, label, null, null, ReasonMesh);
        }

        static GameObject AttachError(GameObject target, string label, string fieldPath, string assetGuid,
            string reasonKind)
        {
            return AttachError(target, label, fieldPath, assetGuid, reasonKind, string.IsNullOrEmpty(fieldPath));
        }

        static GameObject AttachError(GameObject target, string label, string fieldPath, string assetGuid,
            string reasonKind, bool wholeObject)
        {
            if (target == null)
            {
                return null;
            }

            // Field-level errors replace their previous marker instead of stacking: a reconnecting peer
            // re-applies the same snapshot, and forty markers on one object help nobody.
            if (!string.IsNullOrEmpty(fieldPath))
            {
                ClearError(target, fieldPath);
            }

            var bounds = ObjectBounds(target);
            var unit = wholeObject ? bounds.size : bounds.size * 0.5f;
            for (var axis = 0; axis < 3; axis++)
            {
                if (unit[axis] < 0.001f)
                {
                    unit[axis] = 0.001f;
                }
            }

            var body = Create(label, unit);
            if (body == null)
            {
                return null;
            }

            var position = wholeObject
                ? bounds.center
                : bounds.center + Vector3.up * (bounds.size.y * 0.5f + 0.4f);
            body.transform.SetPositionAndRotation(position, Quaternion.identity);

            MoveToSceneOf(body, target);

            var marker = body.AddComponent<CollabMissingMarker>();
            marker.label = label;
            marker.fieldPath = fieldPath;
            marker.reasonKind = reasonKind;
            marker.assetGuid = assetGuid;
            marker.owner = target;
            marker.hideFlags = HideFlags.DontSave;

            if (!Markers.Contains(marker))
            {
                Markers.Add(marker);
            }

            return body;
        }

        /// <summary>Removes the placeholder that belongs to one field, if it is still there.</summary>
        public static bool ClearError(GameObject target, string fieldPath)
        {
            if (target == null || string.IsNullOrEmpty(fieldPath))
            {
                return false;
            }

            var cleared = false;
            for (var i = Markers.Count - 1; i >= 0; i--)
            {
                var marker = Markers[i];
                if (marker == null)
                {
                    Markers.RemoveAt(i);
                    continue;
                }

                if (marker.owner != target || !string.Equals(marker.fieldPath, fieldPath, StringComparison.Ordinal))
                {
                    continue;
                }

                Markers.RemoveAt(i);
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

        /// <summary>Every marker standing in for this object, whole-object and field-level alike.</summary>
        public static CollabMissingMarker[] For(GameObject target)
        {
            if (target == null)
            {
                return new CollabMissingMarker[0];
            }

            var found = new List<CollabMissingMarker>(2);
            for (var i = 0; i < Markers.Count; i++)
            {
                var marker = Markers[i];
                if (marker != null && marker.owner == target)
                {
                    found.Add(marker);
                }
            }

            return found.ToArray();
        }

        /// <summary>Removes every placeholder that stands in for <paramref name="root"/> or its children.</summary>
        public static int Clear(GameObject root)
        {
            if (root == null)
            {
                return 0;
            }

            var removed = 0;
            var rootTransform = root.transform;
            for (var i = Markers.Count - 1; i >= 0; i--)
            {
                var marker = Markers[i];
                if (marker == null)
                {
                    Markers.RemoveAt(i);
                    continue;
                }

                var owner = marker.owner;
                if (owner != root && (owner == null || !owner.transform.IsChildOf(rootTransform)))
                {
                    continue;
                }

                Markers.RemoveAt(i);
                UnityEngine.Object.DestroyImmediate(marker.gameObject);
                removed++;
            }

            return removed;
        }

        /// <summary>
        /// Retires every marker whose reason no longer holds, and orphans of objects that are gone.
        ///
        /// This is the other half of marking, and its absence was a bug users saw: a marker was hung up
        /// while an asset was still importing (or while a prefab's guid was not yet in this project's
        /// database), and once the asset arrived nothing ever took it down. The object then synchronised
        /// perfectly while a magenta ERROR sign kept claiming content was missing. Cheaper than a rescan:
        /// it walks the marker list, not the scene.
        /// </summary>
        /// <summary>
        /// Called when a prefab that was missing has arrived while its stand-in is still in the scene. The
        /// stand-in is an empty object holding the host's transform; leaving it bare now that the asset is
        /// here would turn a loud ERROR sign into a silently wrong object.
        /// </summary>
        public delegate void PrefabReady(GameObject standIn, string prefabGuid);

        public static int Revalidate(PrefabReady prefabReady = null)
        {
            var retired = 0;
            for (var i = Markers.Count - 1; i >= 0; i--)
            {
                var marker = Markers[i];
                if (marker == null)
                {
                    Markers.RemoveAt(i);
                    continue;
                }

                if (marker.owner != null && StillMissing(marker))
                {
                    continue;
                }

                var owner = marker.owner;
                var reason = marker.reasonKind;
                var guid = marker.assetGuid;

                Markers.RemoveAt(i);

                // A marker whose owner died has nothing left to stand in for.
                UnityEngine.Object.DestroyImmediate(marker.gameObject);
                retired++;

                // Only an untouched stand-in is rebuilt: one that grew children has content on it, and
                // throwing that away to re-instantiate a prefab would lose somebody's work.
                if (prefabReady != null && owner != null && reason == ReasonPrefab
                    && !PrefabUtility.IsPartOfPrefabInstance(owner) && owner.transform.childCount == 0)
                {
                    prefabReady(owner, guid);
                }
            }

            return retired;
        }

        static bool StillMissing(CollabMissingMarker marker)
        {
            switch (marker.reasonKind)
            {
                case ReasonPrefab:
                    return !AssetResolves(marker.assetGuid);
                case ReasonReference:
                    return !SubAssetResolves(marker.assetGuid, marker.localFileId);
                case ReasonScript:
                    if (!string.IsNullOrEmpty(marker.scriptType))
                    {
                        return SceneComponentFields.ResolveType(marker.scriptType) == null;
                    }

                    // The scan cannot name the type of a null component; it can only ask again whether the
                    // object still carries one. A compile that brings the script back clears the marker.
                    var components = marker.owner.GetComponents<Component>();
                    for (var i = 0; i < components.Length; i++)
                    {
                        if (components[i] == null)
                        {
                            return true;
                        }
                    }

                    return false;
                case ReasonMesh:
                    return CollabAssetHealth.IsMissingMeshOrMaterial(marker.owner);
                default:
                    // An unknown reason is not evidence that the thing arrived: leave it alone.
                    return true;
            }
        }

        /// <summary>
        /// True when the guid resolves *and* the sub-asset inside it still exists. A reference token names
        /// both; checking only the guid would retire a marker whose target was re-authored away.
        /// </summary>
        static bool SubAssetResolves(string guid, long localFileId)
        {
            if (!AssetResolves(guid))
            {
                return false;
            }

            if (localFileId == 0)
            {
                return true;
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            for (var i = 0; i < assets.Length; i++)
            {
                if (assets[i] == null)
                {
                    continue;
                }

                string assetGuid;
                long assetLocalId;
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(assets[i], out assetGuid, out assetLocalId)
                    && assetLocalId == localFileId)
                {
                    return true;
                }
            }

            return false;
        }

        static bool AssetResolves(string guid)
        {
            if (string.IsNullOrEmpty(guid))
            {
                return false;
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);
            return !string.IsNullOrEmpty(path) && AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path) != null;
        }

        static void MoveToSceneOf(GameObject body, GameObject target)
        {
            var scene = target.scene;
            if (scene.IsValid() && body.scene != scene)
            {
                SceneManager.MoveGameObjectToScene(body, scene);
            }
        }

        static Bounds ObjectBounds(GameObject target)
        {
            var renderers = target.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return new Bounds(target.transform.position, Vector3.one);
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            if (bounds.size.sqrMagnitude < 1e-6f)
            {
                return new Bounds(target.transform.position, Vector3.one);
            }

            return bounds;
        }

        static GameObject BuildCube(string label)
        {
            var box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            if (box == null)
            {
                return null;
            }

            var renderer = box.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = ErrorMaterial();
                renderer.hideFlags = HideFlags.DontSave;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return box;
        }

        /// <summary>
        /// The project's own placeholder prefab, if it configured one. This is why the package does not
        /// bundle a joke model: Half-Life's error sign belongs to Valve, so it stays the project's own file
        /// and the public package ships only its own cube.
        /// </summary>
        static GameObject BuildErrorModel(string label)
        {
            var path = CollabSettings.Load().errorModelPath;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                return null;
            }

            var body = UnityEngine.Object.Instantiate(prefab);
            if (body == null)
            {
                return null;
            }

            body.hideFlags = HideFlags.DontSave;
            StripRecursive(body);
            FitToUnitSize(body);
            return body;
        }

        static void StripRecursive(GameObject body)
        {
            body.hideFlags = HideFlags.DontSave;
            for (var i = 0; i < body.transform.childCount; i++)
            {
                StripRecursive(body.transform.GetChild(i).gameObject);
            }
        }

        /// <summary>
        /// Normalises a model of unknown size to a 1×1×1 footprint so the caller can scale it to whatever it
        /// stands in for, and re-centres it on its own render bounds so the pivot of an imported FBX does not
        /// offset the marker.
        /// </summary>
        static void FitToUnitSize(GameObject body)
        {
            var renderers = body.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return;
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            var longest = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z));
            if (longest <= 0.0001f)
            {
                return;
            }

            var scale = 1f / longest;
            var offset = body.transform.position - bounds.center;
            for (var i = 0; i < renderers.Length; i++)
            {
                var child = renderers[i].transform;
                child.position += offset;
                child.localScale *= scale;
            }
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

            // Emission is the whole point: an ERROR marker has to glow in a dark level, and a plain magenta
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

    /// <summary>
    /// Marks a placeholder so it can be found and reverted later. It is a scene root, not a child of what it
    /// stands in for, so it keeps the owner as a reference rather than as a parent.
    /// </summary>
    [ExecuteAlways]
    public sealed class CollabMissingMarker : MonoBehaviour
    {
        public string label;

        /// <summary>Set for field-level markers; null for whole-object ones.</summary>
        public string fieldPath;

        /// <summary>The object this stands in for. Not the parent: see the class remarks.</summary>
        [NonSerialized]
        public GameObject owner;

        /// <summary>
        /// The sub-asset id the reference token carried, when this marker is about a reference. Zero for the
        /// other reasons.
        /// </summary>
        public long localFileId;

        /// <summary>One of <see cref="CollabPlaceholders.ReasonPrefab"/> and friends.</summary>
        public string reasonKind;

        /// <summary>For asset-backed reasons: the guid that has to appear before this marker can go.</summary>
        public string assetGuid;

        /// <summary>For a missing script: the type name that has to become loadable.</summary>
        public string scriptType;
    }
}
