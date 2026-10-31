using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Which scene is open, and which assets it needs.
    ///
    /// A room is bound to one scene: the host's. Everything here is main-thread only (AssetDatabase
    /// and SceneManager both are), so callers snapshot it before handing work to the socket thread.
    /// </summary>
    public struct SceneIdentity
    {
        public string guid;
        public string path;
        public string name;

        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(guid); }
        }

        /// <summary>
        /// Identity of the scene the user is editing.
        ///
        /// In prefab mode the open "scene" is a throwaway preview scene with no asset path, so the
        /// prefab stage is resolved back to the scene the stage was opened from.
        /// </summary>
        public static SceneIdentity CaptureActive()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && !string.IsNullOrEmpty(stage.assetPath))
            {
                var owningScene = stage.scene;
                var fromStage = FromScene(owningScene);
                if (fromStage.IsValid)
                {
                    return fromStage;
                }
            }

            return FromScene(SceneManager.GetActiveScene());
        }

        public static SceneIdentity FromScene(Scene scene)
        {
            var identity = new SceneIdentity
            {
                path = string.IsNullOrEmpty(scene.path) ? "" : scene.path,
                name = string.IsNullOrEmpty(scene.name) ? "" : scene.name,
                guid = "",
            };

            if (!string.IsNullOrEmpty(identity.path))
            {
                identity.guid = AssetDatabase.AssetPathToGUID(identity.path);
            }

            return identity;
        }

        /// <summary>
        /// Every project asset the scene depends on, as GUIDs.
        ///
        /// Built-in engine resources are excluded: "Resources/unity_builtin_extra" has no GUID asset
        /// behind it and is identical on every install, so counting it as a shared asset would only
        /// produce noise.
        /// </summary>
        public static string[] CollectAssetGuids(string scenePath, int limit)
        {
            if (string.IsNullOrEmpty(scenePath))
            {
                return new string[0];
            }

            var seen = new HashSet<string>();
            var guids = new List<string>();

            string[] dependencies;
            try
            {
                dependencies = AssetDatabase.GetDependencies(scenePath, true);
            }
            catch (System.Exception)
            {
                return new string[0];
            }

            for (var i = 0; i < dependencies.Length; i++)
            {
                var dependency = dependencies[i];
                if (string.IsNullOrEmpty(dependency) || dependency.StartsWith("Resources/"))
                {
                    continue;
                }

                if (!dependency.StartsWith("Assets/") && !dependency.StartsWith("Packages/"))
                {
                    continue;
                }

                var guid = AssetDatabase.AssetPathToGUID(dependency);
                if (string.IsNullOrEmpty(guid) || !seen.Add(guid))
                {
                    continue;
                }

                guids.Add(guid);
                if (limit > 0 && guids.Count >= limit)
                {
                    break;
                }
            }

            return guids.ToArray();
        }

        /// <summary>
        /// GUIDs from <paramref name="assetGuids"/> that this machine cannot resolve to a file.
        /// </summary>
        public static string[] Missing(string[] assetGuids)
        {
            if (assetGuids == null || assetGuids.Length == 0)
            {
                return new string[0];
            }

            var missing = new List<string>();
            for (var i = 0; i < assetGuids.Length; i++)
            {
                var guid = assetGuids[i];
                if (string.IsNullOrEmpty(guid))
                {
                    continue;
                }

                // A guid resolves to a path first; only the asset database can say whether that path
                // still holds an importable asset.
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.LoadAssetAtPath<Object>(path) == null)
                {
                    missing.Add(guid);
                }
            }

            return missing.ToArray();
        }
    }
}
