using System;
using System.Collections.Generic;
using System.IO;
using SceneCollab.Domain;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Maps scene objects to the ids that travel over the wire, and remembers that mapping in a
    /// sidecar next to the scene (<c>Foo.unity.ids.json</c>) so it survives a save.
    ///
    /// The id is a hash of Unity's <c>GlobalObjectId</c> (scene GUID + fileID + prefab context).
    /// fileIDs are not usable directly: <c>SaveScene</c> reassigns them, so two editors that saved
    /// at different times would disagree about which object is which — the exact failure that makes
    /// file-level scene merging hopeless.
    ///
    /// The sidecar holds the hash *and* the raw global id, because a hash cannot be reversed. That
    /// lets a human (or a repair pass) answer "which object is 3f9a…?" after the fact, and makes a
    /// hash collision detectable instead of mysterious.
    /// </summary>
    public sealed class SceneNodeRegistry
    {
        public const string SidecarSuffix = ".ids.json";

        [Serializable]
        public sealed class NodeEntry
        {
            public string nodeId;
            public string globalId;
            public string path;
            public string name;
        }

        [Serializable]
        public sealed class SidecarDocument
        {
            public int version = 1;
            public string scene;
            public NodeEntry[] nodes = new NodeEntry[0];
        }

        readonly Dictionary<GameObject, string> _idByObject = new Dictionary<GameObject, string>();
        readonly Dictionary<string, GameObject> _objectByNode = new Dictionary<string, GameObject>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _globalIdByNode = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _nodeByGlobalId = new Dictionary<string, string>(StringComparer.Ordinal);

        public int Count
        {
            get { return _objectByNode.Count; }
        }

        /// <summary>Id for an object, assigning one on first sight. Returns null when Unity has no stable id yet.</summary>
        public string EnsureNodeId(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            string existing;
            if (_idByObject.TryGetValue(gameObject, out existing))
            {
                return existing;
            }

            string globalId;
            if (!TryGetGlobalIdString(gameObject, out globalId))
            {
                // Not yet persisted (a brand-new, unsaved object). Deliberately no id: inventing one
                // would hand out an identifier that means something else after the next save.
                return null;
            }

            string nodeId;
            if (!_nodeByGlobalId.TryGetValue(globalId, out nodeId))
            {
                nodeId = StableNodeId.FromGlobalObjectId(globalId);
                _nodeByGlobalId[globalId] = nodeId;
                _globalIdByNode[nodeId] = globalId;
            }

            _idByObject[gameObject] = nodeId;
            _objectByNode[nodeId] = gameObject;
            return nodeId;
        }

        public bool TryResolve(string nodeId, out GameObject gameObject)
        {
            gameObject = null;
            if (string.IsNullOrEmpty(nodeId))
            {
                return false;
            }

            // Local, not the out parameter: a failed lookup must leave the caller with a real null, not
            // a destroyed UnityEngine.Object that reads as neither null nor alive.
            GameObject cached;
            if (_objectByNode.TryGetValue(nodeId, out cached) && cached != null)
            {
                gameObject = cached;
                return true;
            }

            _objectByNode.Remove(nodeId);

            // The object may have been reloaded (domain reload, undo of a destroy). Resolve from the
            // stored global id before giving up on it.
            string globalId;
            if (!_globalIdByNode.TryGetValue(nodeId, out globalId))
            {
                return false;
            }

            GlobalObjectId parsed;
            if (!GlobalObjectId.TryParse(globalId, out parsed))
            {
                return false;
            }

            var resolved = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(parsed) as GameObject;
            if (resolved == null)
            {
                return false;
            }

            _objectByNode[nodeId] = resolved;
            _idByObject[resolved] = nodeId;
            gameObject = resolved;
            return true;
        }

        /// <summary>
        /// Binds an object to an id chosen elsewhere (a created object whose id was minted by the
        /// creator). The global id is attached on the next save, when Unity can produce one.
        /// </summary>
        public void Adopt(GameObject gameObject, string nodeId)
        {
            if (gameObject == null || !StableNodeId.IsValid(nodeId))
            {
                return;
            }

            _idByObject[gameObject] = nodeId;
            _objectByNode[nodeId] = gameObject;
        }

        public bool HasNodeId(GameObject gameObject)
        {
            string nodeId;
            return TryGetNodeId(gameObject, out nodeId);
        }

        /// <summary>Existing id for an object, never assigning one.</summary>
        public bool TryGetNodeId(GameObject gameObject, out string nodeId)
        {
            nodeId = null;
            if (gameObject == null)
            {
                return false;
            }

            if (_idByObject.TryGetValue(gameObject, out nodeId))
            {
                return !string.IsNullOrEmpty(nodeId);
            }

            return EnsureNodeId(gameObject) != null && _idByObject.TryGetValue(gameObject, out nodeId);
        }

        /// <summary>Copy of the node → object map, for sweeps that must tolerate destroyed objects.</summary>
        public List<KeyValuePair<string, GameObject>> Entries()
        {
            return new List<KeyValuePair<string, GameObject>>(_objectByNode);
        }

        public string GetGlobalId(string nodeId)
        {
            string globalId;
            return _globalIdByNode.TryGetValue(nodeId, out globalId) ? globalId : null;
        }

        /// <summary>All live objects in the loaded scene(s), depth-first, parents before children.</summary>
        public static IEnumerable<GameObject> EnumerateSceneObjects(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                yield break;
            }

            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                var stack = new Stack<Transform>();
                stack.Push(roots[i].transform);

                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    yield return current.gameObject;

                    for (var child = current.childCount - 1; child >= 0; child--)
                    {
                        stack.Push(current.GetChild(child));
                    }
                }
            }
        }

        public static bool TryGetGlobalIdString(GameObject gameObject, out string globalId)
        {
            globalId = null;

            if (gameObject == null)
            {
                return false;
            }

            // No scene asset means no scene GUID, which means no id another editor could ever agree
            // on. Refusing here is what keeps "unsaved object" from becoming a phantom node.
            var scene = gameObject.scene;
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            {
                return false;
            }

            var identifier = GlobalObjectId.GetGlobalObjectIdSlow(gameObject);
            var text = identifier.ToString();

            // The "null" GlobalObjectId round-trips to the all-zero form and does not parse, which is
            // the only public way to tell it apart (GlobalObjectId.identifierType is internal).
            GlobalObjectId parsed;
            if (string.IsNullOrEmpty(text) || !GlobalObjectId.TryParse(text, out parsed))
            {
                return false;
            }

            globalId = text;
            return true;
        }

        public void Forget(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return;
            }

            string nodeId;
            if (!_idByObject.TryGetValue(gameObject, out nodeId))
            {
                return;
            }

            _idByObject.Remove(gameObject);
            GameObject existing;
            if (_objectByNode.TryGetValue(nodeId, out existing) && (existing == null || ReferenceEquals(existing, gameObject)))
            {
                _objectByNode.Remove(nodeId);
            }
        }

        public void Clear()
        {
            _idByObject.Clear();
            _objectByNode.Clear();
            _globalIdByNode.Clear();
            _nodeByGlobalId.Clear();
        }

        public void Save(string sidecarPath, Scene scene)
        {
            if (string.IsNullOrEmpty(sidecarPath))
            {
                return;
            }

            var entries = new List<NodeEntry>();
            foreach (var gameObject in EnumerateSceneObjects(scene))
            {
                var nodeId = EnsureNodeId(gameObject);
                if (nodeId == null)
                {
                    continue;
                }

                // An object created during the session was adopted under a minted id and had no global
                // id until now; after this save it does, and pinning it here is what makes the id
                // survive the next reload.
                var globalId = GetGlobalId(nodeId);
                if (string.IsNullOrEmpty(globalId) && TryGetGlobalIdString(gameObject, out var fresh))
                {
                    _globalIdByNode[nodeId] = fresh;
                    _nodeByGlobalId[fresh] = nodeId;
                    globalId = fresh;
                }

                entries.Add(new NodeEntry
                {
                    nodeId = nodeId,
                    globalId = globalId,
                    path = GetHierarchyPath(gameObject),
                    name = gameObject.name,
                });
            }

            var document = new SidecarDocument { scene = scene.path, nodes = entries.ToArray() };

            try
            {
                var directory = Path.GetDirectoryName(sidecarPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(sidecarPath, JsonUtility.ToJson(document, true));
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SceneCollab] could not write " + sidecarPath + ": " + ex.Message);
            }
        }

        /// <summary>
        /// Loads the sidecar when present. The mapping is advisory: the hash is authoritative, so a
        /// stale sidecar can only warn, never misidentify — which is why a missing or corrupt file is
        /// not an error.
        /// </summary>
        public void Load(string sidecarPath)
        {
            if (string.IsNullOrEmpty(sidecarPath) || !File.Exists(sidecarPath))
            {
                return;
            }

            try
            {
                var document = JsonUtility.FromJson<SidecarDocument>(File.ReadAllText(sidecarPath));
                if (document == null || document.nodes == null)
                {
                    return;
                }

                for (var i = 0; i < document.nodes.Length; i++)
                {
                    var entry = document.nodes[i];
                    if (entry == null || !StableNodeId.IsValid(entry.nodeId) || string.IsNullOrEmpty(entry.globalId))
                    {
                        continue;
                    }

                    string existing;
                    if (_nodeByGlobalId.TryGetValue(entry.globalId, out existing) && existing != entry.nodeId)
                    {
                        Debug.LogWarning("[SceneCollab] sidecar disagrees with the hash for " + entry.globalId
                            + " (" + entry.nodeId + " vs " + existing + "); trusting the hash.");
                        continue;
                    }

                    _nodeByGlobalId[entry.globalId] = entry.nodeId;
                    _globalIdByNode[entry.nodeId] = entry.globalId;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SceneCollab] could not read " + sidecarPath + ": " + ex.Message);
            }
        }

        /// <summary>Sidecar path for a scene asset (<c>Assets/Scenes/A.unity</c> → <c>…/A.unity.ids.json</c>).</summary>
        public static string SidecarPathFor(Scene scene)
        {
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            {
                return null;
            }

            return scene.path + SidecarSuffix;
        }

        public static string GetHierarchyPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "";
            }

            var parts = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                parts.Add(current.name);
                current = current.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }
    }
}
