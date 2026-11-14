using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>What the guard decided about one save request.</summary>
    public enum SaveGuardDecision
    {
        /// <summary>Nobody is stopping this save.</summary>
        Allow,

        /// <summary>The save is scoped to assets the guard does not police (materials, prefabs).</summary>
        AllowNotScene,

        /// <summary>A guest tried to write the scene file: only the host writes it.</summary>
        BlockGuest,

        /// <summary>The new host is still catching up after promotion: writing now would freeze a partial scene.</summary>
        BlockHostVerifying,
        BlockRecovering,
    }

    /// <summary>
    /// The decision table, separated from Unity so it can be tested without an editor.
    /// </summary>
    public static class SaveGuardPolicy
    {
        public static SaveGuardDecision Decide(
            bool enabled,
            bool connected,
            bool isHost,
            bool hostVerified,
            string assetPath)
        {
            if (!enabled || !connected || !SceneSaveGuard.IsScenePath(assetPath))
            {
                return SceneSaveGuard.IsScenePath(assetPath)
                    ? SaveGuardDecision.Allow
                    : SaveGuardDecision.AllowNotScene;
            }

            if (!isHost)
            {
                return SaveGuardDecision.BlockGuest;
            }

            return hostVerified ? SaveGuardDecision.Allow : SaveGuardDecision.BlockHostVerifying;
        }

        public static string Describe(SaveGuardDecision decision, string path)
        {
            switch (decision)
            {
                case SaveGuardDecision.BlockGuest:
                    return "save of " + path + " cancelled: only the host writes the scene file.";
                case SaveGuardDecision.BlockHostVerifying:
                    return "save of " + path + " held: this editor was just promoted to host and is still catching up.";
                case SaveGuardDecision.BlockRecovering:
                    return "save of " + path + " held: collaboration is disconnected or resynchronising.";
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Keeps the scene file out of the wrong hands, and reports the writes it could not stop.
    ///
    /// Two hands are wrong. A guest: only one editor writes Git, or the second Ctrl+S silently replaces
    /// everyone's work with its own view. A brand-new host: it has the right, but not yet the complete
    /// scene, so <see cref="HostNotVerified"/> holds the save until the room has gone quiet.
    ///
    /// <c>OnWillSaveAssets</c> is the cancel hook and covers the menu, the keyboard and any tool that
    /// saves through the asset pipeline. It is not the whole story: a tool that serialises the scene
    /// itself and writes the file bypasses it entirely, and no public Unity API can veto that. So the
    /// guard also listens to <c>sceneSaving</c>/<c>sceneSaved</c> and compares the file's hash with the
    /// one it last saw. A write that got through while a block was in force is reported loudly instead
    /// of being discovered in the next merge.
    /// </summary>
    public sealed class SceneSaveGuard : AssetModificationProcessor
    {
        /// <summary>Set by the sync layer: true only when connected and not the host.</summary>
        public static bool BlockNonScribeSaves;

        /// <summary>Set by the sync layer: a promoted host whose scene is not verified complete yet.</summary>
        public static bool HostNotVerified;
        public static bool RecoveryBlocked;

        /// <summary>Raised when a save was cancelled or held, so the UI can say who may save.</summary>
        public static event System.Action<string> SaveBlocked;

        /// <summary>Raised when a scene write happened despite the guard: the workspace may be stale.</summary>
        public static event System.Action<string> UnsafeWriteDetected;

        /// <summary>Writes that reached the file while blocked. Should stay zero.</summary>
        public static int UnsafeWrites { get; private set; }

        static readonly Dictionary<string, string> KnownHashes = new Dictionary<string, string>();
        static readonly HashSet<string> BlockedThisBurst = new HashSet<string>();
        static bool _installed;

        public static SaveGuardDecision LastDecision { get; private set; }

        public static string LastPath { get; private set; }

        /// <summary>Hashes a scene file. Empty when the file cannot be read.</summary>
        public static string HashOf(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return string.Empty;
                }

                using (var stream = File.OpenRead(path))
                using (var sha = SHA1.Create())
                {
                    return System.BitConverter.ToString(sha.ComputeHash(stream));
                }
            }
            catch (IOException)
            {
                return string.Empty;
            }
        }

        /// <summary>Subscribes the file-level hooks. Idempotent; called by the sync layer on enable.</summary>
        public static void Install()
        {
            if (_installed)
            {
                return;
            }

            _installed = true;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        public static void Uninstall()
        {
            if (!_installed)
            {
                return;
            }

            _installed = false;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            BlockedThisBurst.Clear();
        }

        /// <summary>Forgets hashes: stopping sync or opening another workspace.</summary>
        public static void Reset()
        {
            KnownHashes.Clear();
            BlockedThisBurst.Clear();
            UnsafeWrites = 0;
            LastDecision = SaveGuardDecision.Allow;
            LastPath = null;
        }

        static void OnSceneSaving(Scene scene, string path)
        {
            if (!IsScenePath(path))
            {
                return;
            }

            var decision = RecoveryBlocked ? SaveGuardDecision.BlockRecovering : SaveGuardPolicy.Decide(
                BlockNonScribeSaves || HostNotVerified,
                true,
                !BlockNonScribeSaves,
                !HostNotVerified,
                path);

            LastDecision = decision;
            LastPath = path;

            if (decision == SaveGuardDecision.BlockGuest || decision == SaveGuardDecision.BlockHostVerifying
                || decision == SaveGuardDecision.BlockRecovering)
            {
                // OnWillSaveAssets may already have cancelled this one; remember the path so
                // OnSceneSaved can tell "cancelled" from "a tool wrote straight to disk".
                BlockedThisBurst.Add(path);
                return;
            }

            // An allowed save clears any earlier block on this path, otherwise a real save later in the
            // same session would be reported as a bypass.
            BlockedThisBurst.Remove(path);
            KnownHashes[path] = HashOf(path);
        }

        static void OnSceneSaved(Scene scene)
        {
            // Unity's callback carries the scene, not the path; a scene with no path was never written.
            var path = scene.IsValid() ? scene.path : string.Empty;
            if (!IsScenePath(path))
            {
                return;
            }

            if (BlockedThisBurst.Remove(path))
            {
                UnsafeWrites++;
                var handler = UnsafeWriteDetected;
                if (handler != null)
                {
                    handler(path);
                }

                Debug.LogError(
                    "[SceneCollab] " + path + " was written while this editor was blocked from saving. "
                    + "The file on disk may not reflect the session; do not commit it before re-opening the scene.");
            }

            KnownHashes[path] = HashOf(path);
        }

        /// <summary>
        /// Notices a scene file that changed underneath us without a save this editor initiated —
        /// another editor instance, a script, a Git checkout. Reported, never auto-reverted.
        /// </summary>
        public static void CheckExternalWrite(string path)
        {
            if (!IsScenePath(path))
            {
                return;
            }

            var current = HashOf(path);
            if (string.IsNullOrEmpty(current))
            {
                return;
            }

            string known;
            if (!KnownHashes.TryGetValue(path, out known))
            {
                // First look at this file: nothing to compare against, so record rather than accuse.
                KnownHashes[path] = current;
                return;
            }

            if (known == current)
            {
                return;
            }

            KnownHashes[path] = current;
            var handler = UnsafeWriteDetected;
            if (handler != null)
            {
                handler(path);
            }

            Debug.LogWarning(
                "[SceneCollab] " + path + " changed outside this editor's save. "
                + "If another tool rewrote the scene, reload it before trusting the session state.");
        }

        static string[] OnWillSaveAssets(string[] paths)
        {
            if (paths == null || paths.Length == 0 || (!BlockNonScribeSaves && !HostNotVerified && !RecoveryBlocked))
            {
                return paths;
            }

            var kept = new List<string>(paths.Length);
            var blocked = new List<string>();

            for (var i = 0; i < paths.Length; i++)
            {
                var terrainAsset = AssetDatabase.GetMainAssetTypeAtPath(paths[i]) == typeof(TerrainData);
                if (!IsScenePath(paths[i]) && !terrainAsset)
                {
                    kept.Add(paths[i]);
                    continue;
                }

                if (RecoveryBlocked || (terrainAsset && (BlockNonScribeSaves || HostNotVerified)))
                {
                    LastDecision = RecoveryBlocked ? SaveGuardDecision.BlockRecovering
                        : BlockNonScribeSaves ? SaveGuardDecision.BlockGuest : SaveGuardDecision.BlockHostVerifying;
                    LastPath = paths[i];
                    blocked.Add(paths[i]);
                    continue;
                }

                var decision = SaveGuardPolicy.Decide(
                    true,
                    true,
                    !BlockNonScribeSaves,
                    !HostNotVerified,
                    paths[i]);

                LastDecision = decision;
                LastPath = paths[i];

                if (decision == SaveGuardDecision.BlockGuest || decision == SaveGuardDecision.BlockHostVerifying)
                {
                    blocked.Add(paths[i]);
                    continue;
                }

                kept.Add(paths[i]);
            }

            if (blocked.Count == 0)
            {
                return paths;
            }

            var handler = SaveBlocked;
            for (var i = 0; i < blocked.Count; i++)
            {
                var reason = SaveGuardPolicy.Describe(LastDecision, blocked[i]);
                // Describe only needs to know which block applies; both reasons are per-path identical.

                if (handler != null)
                {
                    handler(reason);
                }

                Debug.LogWarning("[SceneCollab] " + reason);
                BlockedThisBurst.Add(blocked[i]);
            }

            return kept.ToArray();
        }

        public static bool IsScenePath(string path)
        {
            return !string.IsNullOrEmpty(path) && path.EndsWith(".unity", System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
