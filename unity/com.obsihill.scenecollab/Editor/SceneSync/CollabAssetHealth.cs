using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// The "my checkout is incomplete" report, as the scene shows it.
    ///
    /// Unity's silent failures are the reason this exists: a renderer whose mesh is gone draws nothing,
    /// so a peer's object is simply not there; a script whose assembly is missing becomes an
    /// uninspectable placeholder. Both look identical to "the other person deleted it". The scan below
    /// finds those objects and hangs a marker on them, so the answer to "where did it go?" is visible in
    /// the scene instead of in a log.
    ///
    /// The scan is the *marking* half. Retiring a marker when the content arrives is
    /// <see cref="CollabPlaceholders.Revalidate"/>, and skipping the scan while the editor is compiling or
    /// importing is what stops false positives: mid-import a perfectly good prefab looks empty, and a
    /// marker hung up in that window used to stay forever.
    /// </summary>
    public static class CollabAssetHealth
    {
        public struct Report
        {
            public int MissingScripts;
            public int MissingMeshes;
            public int Placeholders;
        }

        /// <summary>
        /// Looks for objects whose content this machine cannot draw, and marks each one once.
        /// <paramref name="missingGuids"/> comes from the host's manifest and is used for the report
        /// count; the per-object pass is independent of it, because a guid list cannot tell which
        /// renderer lost its mesh.
        /// </summary>
        public static Report Scan(Scene scene, string[] missingGuids)
        {
            var report = new Report();

            // Compiling or importing makes every script look missing and every fresh prefab look empty.
            // Marking then produces ERROR signs on content that is on its way in.
            if (!scene.IsValid() || !scene.isLoaded || EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return report;
            }

            foreach (var gameObject in SceneNodeRegistry.EnumerateSceneObjects(scene))
            {
                if (gameObject == null || gameObject.hideFlags == HideFlags.DontSave
                    || CollabPlaceholders.IsPlaceholder(gameObject))
                {
                    continue;
                }

                var components = gameObject.GetComponents<Component>();

                for (var i = 0; i < components.Length; i++)
                {
                    var component = components[i];
                    if (component == null)
                    {
                        report.MissingScripts++;
                        if (MarkScript(gameObject) != null)
                        {
                            report.Placeholders++;
                        }

                        continue;
                    }

                    var renderer = component as Renderer;
                    if (renderer == null || !IsMissingMeshOrMaterial(gameObject, renderer))
                    {
                        continue;
                    }

                    report.MissingMeshes++;
                    if (MarkMesh(gameObject) != null)
                    {
                        report.Placeholders++;
                    }
                }
            }

            return report;
        }

        /// <summary>
        /// True when this renderer has nothing to draw with: no mesh where one is expected, or no material
        /// in the first slot. Shared with the marker refresh pass so marking and retiring use one predicate.
        /// </summary>
        public static bool IsMissingMeshOrMaterial(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return false;
            }

            var renderers = gameObject.GetComponents<Renderer>();
            for (var i = 0; i < renderers.Length; i++)
            {
                var renderer = renderers[i];
                if (renderer != null && IsMissingMeshOrMaterial(gameObject, renderer))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsMissingMeshOrMaterial(GameObject gameObject, Renderer renderer)
        {
            // Only the renderer types that carry a mesh/ material pair of their own. A ParticleSystemRenderer
            // without a material is a different (and legitimate) state.
            if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
            {
                return false;
            }

            if (renderer is SkinnedMeshRenderer)
            {
                if (((SkinnedMeshRenderer)renderer).sharedMesh == null)
                {
                    return true;
                }
            }
            else
            {
                var filter = gameObject.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh == null)
                {
                    return true;
                }
            }

            // A renderer with no material renders magenta in the editor but nothing in a build; either way
            // the peer's object does not look like the host's.
            var materials = renderer.sharedMaterials;
            return materials == null || materials.Length == 0 || materials[0] == null;
        }

        static GameObject MarkMesh(GameObject gameObject)
        {
            if (CollabPlaceholders.For(gameObject).Length > 0)
            {
                return null;
            }

            return CollabPlaceholders.AttachMeshError(gameObject, "missing mesh or material");
        }

        static GameObject MarkScript(GameObject gameObject)
        {
            // Marking is idempotent by inspection rather than by bookkeeping: a second marker for the same
            // object would mean the scan ran twice, and the marker itself is the evidence.
            if (CollabPlaceholders.For(gameObject).Length > 0)
            {
                return null;
            }

            // The type name is not knowable here — Unity hands back a null component and nothing else — so
            // this marker re-checks "does this object still have an unresolvable component" instead.
            return CollabPlaceholders.AttachScriptError(gameObject, "missing script", null);
        }

        /// <summary>Retires markers whose content has since arrived. Cheap enough to call on a timer.</summary>
        public static int Revalidate()
        {
            return CollabPlaceholders.Revalidate();
        }
    }
}
