using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Hierarchy changes: objects created, destroyed, re-parented, re-ordered.
    ///
    /// These are not property edits, so the property diff cannot see them; and Unity reports them
    /// through a different channel (<c>ObjectChangeEvents</c>), with a payload that names instance ids
    /// rather than anything stable. So the watch is coarse — "something structural happened" — and the
    /// response is a sweep of the registry, which is small (one entry per object) and only runs after a
    /// structural event.
    ///
    /// Node ids for newly created objects: Unity gives an unsaved object no stable id, so the creator
    /// mints one and both sides adopt it. The host's next save pins that id to the object's real
    /// GlobalObjectId in the sidecar, which is what keeps the id valid across reloads.
    /// </summary>
    public sealed class SceneStructure
    {
        [Serializable]
        public sealed class AddNodePayload
        {
            public string name;
            public bool active;
            public float px;
            public float py;
            public float pz;
            public float rx;
            public float ry;
            public float rz;
            public float rw;
            public float sx;
            public float sy;
            public float sz;
        }

        readonly SceneNodeRegistry _registry;
        readonly Dictionary<string, string> _parentByNode = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly Dictionary<string, int> _indexByNode = new Dictionary<string, int>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _componentsByNode = new Dictionary<string, string>(StringComparer.Ordinal);

        public SceneStructure(SceneNodeRegistry registry)
        {
            _registry = registry;
        }

        public const string RootParent = "";

        /// <summary>Mints a node id for an object this editor is creating.</summary>
        public static string NewNodeId()
        {
            // Same shape as StableNodeId (16 lowercase hex), so the server's validator accepts it and
            // nothing downstream has to special-case "new" ids.
            return Guid.NewGuid().ToString("N").Substring(0, 16);
        }

        public void Baseline(GameObject gameObject, string nodeId)
        {
            if (gameObject == null || string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            string parentNodeId;
            _parentByNode[nodeId] = TryGetParentNodeId(gameObject, out parentNodeId) ? parentNodeId : null;
            _indexByNode[nodeId] = gameObject.transform.GetSiblingIndex();
            _componentsByNode[nodeId] = ComponentSignature(gameObject);
        }

        public void Forget(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            _parentByNode.Remove(nodeId);
            _indexByNode.Remove(nodeId);
            _componentsByNode.Remove(nodeId);
        }

        public void Clear()
        {
            _parentByNode.Clear();
            _indexByNode.Clear();
            _componentsByNode.Clear();
        }

        public int Tracked
        {
            get { return _parentByNode.Count; }
        }

        /// <summary>Node ids the registry knows about whose object is gone from the scene.</summary>
        public List<string> DetectRemoved()
        {
            var removed = new List<string>();
            var entries = _registry.Entries();

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Value == null)
                {
                    removed.Add(entry.Key);
                }
            }

            return removed;
        }

        /// <summary>
        /// Objects present in the scene that have no node id yet. The caller decides whether they are
        /// new (and mints an id) or simply unsaved-by-the-host objects that predate the session.
        /// </summary>
        public List<GameObject> DetectUnregistered(Scene scene)
        {
            var unregistered = new List<GameObject>();

            foreach (var gameObject in SceneNodeRegistry.EnumerateSceneObjects(scene))
            {
                if (gameObject == null || _registry.HasNodeId(gameObject))
                {
                    continue;
                }

                unregistered.Add(gameObject);
            }

            return unregistered;
        }

        public SceneOperation BuildAdd(GameObject gameObject, string nodeId, out bool created)
        {
            created = false;

            string parentNodeId;
            if (!TryGetParentNodeId(gameObject, out parentNodeId))
            {
                // Parent is not part of the session either; it will be reported with its own add op.
                return default(SceneOperation);
            }

            var prefabGuid = PrefabGuidOf(gameObject);
            var payload = Capture(gameObject);

            _registry.Adopt(gameObject, nodeId);
            Baseline(gameObject, nodeId);

            SceneOperation operation;
            string error;
            if (!SceneOperation.TryCreate(
                    OpKinds.AddNode,
                    nodeId,
                    "",
                    null,
                    JsonUtility.ToJson(payload),
                    out operation,
                    out error))
            {
                return default(SceneOperation);
            }

            operation.ParentNodeId = parentNodeId;
            operation.SiblingIndex = gameObject.transform.GetSiblingIndex();
            operation.PrefabGuid = prefabGuid ?? "";

            if (string.IsNullOrEmpty(prefabGuid))
            {
                // The add payload carries only name / active / transform. Every component has to
                // follow as its own add_component op, so the signature starts empty here: baselining
                // the real one told the next sweep there was nothing to send, and the peer got a bare
                // object with a Transform — a cube that is never visible on the other screen.
                _componentsByNode[nodeId] = string.Empty;
            }

            created = true;
            return operation;
        }

        /// <summary>
        /// Sorted, duplicate-preserving list of the component type names on an object.
        ///
        /// Adding or removing a component fires no property modification and no hierarchy change, so
        /// neither the property diff nor the hierarchy sweep can see it. Comparing this signature on
        /// every structural sweep is the cheap way to notice. Duplicates are kept: Unity allows two
        /// colliders on one object, and a set would report that second collider as no change at all.
        /// </summary>
        public static string ComponentSignature(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return string.Empty;
            }

            var components = gameObject.GetComponents<Component>();
            var names = new List<string>(components.Length);
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                // A component whose script is missing comes back null; it is still a real entry in the
                // scene, and pretending it is absent would emit a duplicate add every sweep.
                names.Add(component == null ? "<missing>" : component.GetType().FullName);
            }

            names.Sort(StringComparer.Ordinal);
            return string.Join("|", names.ToArray());
        }

        /// <summary>
        /// Component ops since the last <see cref="Baseline"/> for this node: additions and removals.
        /// Transform is never reported — Unity does not allow removing it, and every object has one.
        /// </summary>
        public List<SceneOperation> CollectComponentChanges(GameObject gameObject, string nodeId)
        {
            var operations = new List<SceneOperation>();
            if (gameObject == null || string.IsNullOrEmpty(nodeId))
            {
                return operations;
            }

            var current = ComponentSignature(gameObject);

            string previous;
            if (!_componentsByNode.TryGetValue(nodeId, out previous))
            {
                _componentsByNode[nodeId] = current;
                return operations;
            }

            if (string.Equals(previous, current, StringComparison.Ordinal))
            {
                return operations;
            }

            var before = CountSignature(previous);
            var after = CountSignature(current);

            foreach (var pair in before)
            {
                if (pair.Key == typeof(Transform).FullName)
                {
                    continue;
                }

                int removed;
                after.TryGetValue(pair.Key, out removed);
                var surplus = pair.Value - removed;
                for (var i = 0; i < surplus; i++)
                {
                    SceneOperation operation;
                    string error;
                    if (!SceneOperation.TryCreate(OpKinds.RemoveComponent, nodeId, "", pair.Key, "", out operation, out error))
                    {
                        continue;
                    }

                    operation.ComponentType = pair.Key;
                    operations.Add(operation);
                }
            }

            foreach (var pair in after)
            {
                if (pair.Key == typeof(Transform).FullName || pair.Key == "<missing>")
                {
                    continue;
                }

                int existed;
                before.TryGetValue(pair.Key, out existed);
                var surplus = pair.Value - existed;
                for (var i = 0; i < surplus; i++)
                {
                    SceneOperation operation;
                    string error;
                    if (!SceneOperation.TryCreate(OpKinds.AddComponent, nodeId, "component", "", pair.Key, out operation, out error))
                    {
                        continue;
                    }

                    operation.ComponentType = pair.Key;
                    operations.Add(operation);
                }
            }

            _componentsByNode[nodeId] = current;
            return operations;
        }

        static Dictionary<string, int> CountSignature(string signature)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(signature))
            {
                return counts;
            }

            var parts = signature.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                int seen;
                counts.TryGetValue(parts[i], out seen);
                counts[parts[i]] = seen + 1;
            }

            return counts;
        }

        public SceneOperation BuildRemove(string nodeId, string name)
        {
            SceneOperation operation;
            string error;
            if (!SceneOperation.TryCreate(OpKinds.RemoveNode, nodeId, "", name, "", out operation, out error))
            {
                return default(SceneOperation);
            }

            Forget(nodeId);
            return operation;
        }

        /// <summary>
        /// Detects a parent change or a sibling-index change for a tracked object, and records the new
        /// position as the baseline so the same change is not reported twice.
        /// </summary>
        public bool TryCollectMove(GameObject gameObject, string nodeId, out SceneOperation operation)
        {
            operation = default(SceneOperation);

            if (gameObject == null || string.IsNullOrEmpty(nodeId))
            {
                return false;
            }

            string parentNodeId;
            if (!TryGetParentNodeId(gameObject, out parentNodeId))
            {
                return false;
            }

            var index = gameObject.transform.GetSiblingIndex();

            string knownParent;
            var hasParent = _parentByNode.TryGetValue(nodeId, out knownParent);
            int knownIndex;
            var hasIndex = _indexByNode.TryGetValue(nodeId, out knownIndex);

            if (!hasParent || !hasIndex)
            {
                Baseline(gameObject, nodeId);
                return false;
            }

            if (string.Equals(knownParent, parentNodeId, StringComparison.Ordinal) && knownIndex == index)
            {
                return false;
            }

            var kind = string.Equals(knownParent, parentNodeId, StringComparison.Ordinal)
                ? OpKinds.ReorderChildren
                : OpKinds.Reparent;

            SceneOperation created;
            string error;
            if (!SceneOperation.TryCreate(kind, nodeId, "", knownParent ?? RootParent, parentNodeId, out created, out error))
            {
                return false;
            }

            created.ParentNodeId = parentNodeId;
            created.SiblingIndex = index;

            _parentByNode[nodeId] = parentNodeId;
            _indexByNode[nodeId] = index;
            operation = created;
            return true;
        }

        public static AddNodePayload Capture(GameObject gameObject)
        {
            var transform = gameObject.transform;
            var rotation = transform.localRotation;
            var scale = transform.localScale;
            var position = transform.localPosition;

            return new AddNodePayload
            {
                name = gameObject.name,
                active = gameObject.activeSelf,
                px = position.x,
                py = position.y,
                pz = position.z,
                rx = rotation.x,
                ry = rotation.y,
                rz = rotation.z,
                rw = rotation.w,
                sx = scale.x,
                sy = scale.y,
                sz = scale.z,
            };
        }

        /// <summary>
        /// Asset guid of the prefab an object stands for, when it is the root of a prefab instance.
        /// Nested children carry their own overrides, which the owning prefab instance already covers.
        /// </summary>
        public static string PrefabGuidOf(GameObject gameObject)
        {
            if (gameObject == null || !PrefabUtility.IsPartOfPrefabInstance(gameObject))
            {
                return "";
            }

            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(gameObject))
            {
                return "";
            }

            var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            if (string.IsNullOrEmpty(path))
            {
                return "";
            }

            return AssetDatabase.AssetPathToGUID(path);
        }

        bool TryGetParentNodeId(GameObject gameObject, out string parentNodeId)
        {
            parentNodeId = RootParent;

            var parent = gameObject.transform.parent;
            if (parent == null)
            {
                return true;
            }

            return _registry.TryGetNodeId(parent.gameObject, out parentNodeId);
        }
    }
}
