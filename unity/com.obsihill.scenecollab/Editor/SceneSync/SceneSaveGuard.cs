using System.IO;
using UnityEditor;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Stops every editor except the scribe from writing the scene file.
    ///
    /// This is the rule that makes the whole design work: the server holds the truth, but Git only
    /// ever sees what the scribe saved. If two editors both hit Ctrl+S, the second one silently
    /// rewrites the file with its own (incomplete) view of everyone's changes — no error, no conflict
    /// marker, just lost work. Returning an empty array from <c>OnWillSaveAssets</c> cancels the save.
    ///
    /// Non-scene assets are never blocked: saving a material or a prefab in your own editor is your
    /// business.
    /// </summary>
    public sealed class SceneSaveGuard : AssetModificationProcessor
    {
        /// <summary>Set by the sync layer: true only when connected and not the scribe.</summary>
        public static bool BlockNonScribeSaves;

        /// <summary>Raised when a save was cancelled, so the UI can say who is allowed to save.</summary>
        public static event System.Action<string> SaveBlocked;

        static string[] OnWillSaveAssets(string[] paths)
        {
            if (!BlockNonScribeSaves || paths == null || paths.Length == 0)
            {
                return paths;
            }

            var kept = new System.Collections.Generic.List<string>(paths.Length);
            string blocked = null;

            for (var i = 0; i < paths.Length; i++)
            {
                if (!IsScenePath(paths[i]))
                {
                    kept.Add(paths[i]);
                    continue;
                }

                blocked = paths[i];
            }

            if (blocked == null)
            {
                return paths;
            }

            var handler = SaveBlocked;
            if (handler != null)
            {
                handler(blocked);
            }

            UnityEngine.Debug.LogWarning(
                "[SceneCollab] save of " + blocked + " cancelled: only the scribe writes the scene file.");

            return kept.ToArray();
        }

        public static bool IsScenePath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
