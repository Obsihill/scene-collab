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
    /// finds those objects and hangs a magenta marker on them, so the answer to "where did it go?" is
    /// visible in the scene instead of in a log.
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
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return report;
            }

            foreach (var gameObject in SceneNodeRegistry.EnumerateSceneObjects(scene))
            {
                if (gameObject == null || gameObject.hideFlags == HideFlags.DontSave)
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
                        if (Mark(gameObject, "missing script"))
                        {
                            report.Placeholders++;
                        }

                        continue;
                    }

                    var renderer = component as Renderer;
                    if (renderer == null)
                    {
                        continue;
                    }

                    var meshesMissing = false;
                    if (renderer is MeshRenderer || renderer is SkinnedMeshRenderer)
                    {
                        meshesMissing = renderer is MeshRenderer
                            ? gameObject.GetComponent<MeshFilter>() != null
                              && gameObject.GetComponent<MeshFilter>().sharedMesh == null
                            : ((SkinnedMeshRenderer)renderer).sharedMesh == null;

                        // A renderer with no material renders magenta in the editor but nothing in a build;
                        // either way the peer's object does not look like the host's.
                        if (renderer.sharedMaterials == null || renderer.sharedMaterials.Length == 0
                            || renderer.sharedMaterials[0] == null)
                        {
                            meshesMissing = true;
                        }
                    }

                    if (meshesMissing)
                    {
                        report.MissingMeshes++;
                        if (Mark(gameObject, "missing mesh or material"))
                        {
                            report.Placeholders++;
                        }
                    }
                }
            }

            return report;
        }

        static bool Mark(GameObject gameObject, string reason)
        {
            // Marking is idempotent by inspection rather than by bookkeeping: a second marker under the
            // same object would mean the scan ran twice, and the marker itself is the evidence.
            if (CollabPlaceholders.For(gameObject).Length > 0)
            {
                return false;
            }

            return CollabPlaceholders.Attach(gameObject, reason) != null;
        }
    }
}
