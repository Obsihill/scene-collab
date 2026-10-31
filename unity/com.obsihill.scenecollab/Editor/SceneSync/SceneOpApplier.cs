using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Applies an op that came from another editor.
    ///
    /// The server is the total order, so a remote op is applied even when the local object has drifted
    /// from the op's <c>oldValue</c> — but the drift is counted and reported, because silently
    /// overwriting someone's local edit is exactly the failure mode this project exists to avoid.
    /// </summary>
    public sealed class SceneOpApplier
    {
        /// <summary>
        /// Which keys an incoming add-node payload carries.
        ///
        /// A created object cannot be described by its id alone — the receiving editor has never seen it —
        /// so the creator ships the minimum needed to rebuild a stand-in: name, transform, active flag and,
        /// when the object came from a prefab, the prefab's guid. Everything else (components, children,
        /// overrides) arrives as follow-up ops.
        /// </summary>
        [Serializable]
        public sealed class AddNodePayload
        {
            public string name;
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
            public bool active = true;
        }

        readonly SceneNodeRegistry _registry;
        readonly SceneDiff _diff;
        readonly SceneStructure _structure;
        readonly List<GameObject> _touched = new List<GameObject>();

        public SceneOpApplier(SceneNodeRegistry registry, SceneDiff diff, SceneStructure structure)
        {
            _registry = registry;
            _diff = diff;
            _structure = structure;
        }

        public int Applied { get; private set; }

        public int Ignored { get; private set; }

        /// <summary>Ops applied on top of a value the local editor had already changed differently.</summary>
        public int Overwritten { get; private set; }

        /// <summary>Objects this applier created, so the caller can leave them out of its own diff.</summary>
        public IList<GameObject> Touched
        {
            get { return _touched; }
        }

        public bool Apply(WireMessage op, out string note)
        {
            note = null;

            if (op == null)
            {
                note = "empty op";
                return false;
            }

            switch (op.opKind)
            {
                case OpKinds.SetProperty:
                    return ApplyProperty(op, out note);

                case OpKinds.AddNode:
                    return ApplyAddNode(op, out note);

                case OpKinds.RemoveNode:
                    return ApplyRemoveNode(op, out note);

                case OpKinds.Reparent:
                    return ApplyReparent(op, out note);

                case OpKinds.ReorderChildren:
                    return ApplyReorder(op, out note);

                case OpKinds.AddComponent:
                    return ApplyAddComponent(op, out note);

                case OpKinds.SetPrefabOverride:
                    Ignored++;
                    note = "prefab override needs the prefab instance (v0.4)";
                    return false;

                default:
                    Ignored++;
                    note = "unsupported kind: " + op.opKind;
                    return false;
            }
        }

        bool ApplyProperty(WireMessage op, out string note)
        {
            note = null;

            if (string.IsNullOrEmpty(op.propertyPath) || !ScenePropertyAccess.IsTracked(op.propertyPath))
            {
                Ignored++;
                note = "untracked property: " + op.propertyPath;
                return false;
            }

            GameObject gameObject;
            if (!_registry.TryResolve(op.nodeId, out gameObject) || gameObject == null)
            {
                Ignored++;
                note = "unknown node: " + op.nodeId;
                return false;
            }

            var known = _diff.GetKnownValue(op.nodeId, op.propertyPath);
            if (known != null && !SceneValueCodec.AreEqual(known, op.oldValue))
            {
                Overwritten++;
            }

            if (!ScenePropertyAccess.TryWrite(gameObject, op.propertyPath, op.newValue))
            {
                Ignored++;
                note = "could not write " + op.propertyPath;
                return false;
            }

            // Advance the snapshot so the change we just made does not look local.
            _diff.NoteRemoteValue(op.nodeId, op.propertyPath, op.newValue);

            MarkDirty(gameObject.scene);
            Applied++;
            return true;
        }

        bool ApplyAddNode(WireMessage op, out string note)
        {
            note = null;

            GameObject existing;
            if (_registry.TryResolve(op.nodeId, out existing) && existing != null)
            {
                // Already have it: the op is a replay after a reconnect, and rebuilding would duplicate.
                Applied++;
                note = "already present";
                return true;
            }

            if (string.IsNullOrEmpty(op.nodeId))
            {
                Ignored++;
                note = "add without node id";
                return false;
            }

            // The registry will not bind an id it cannot trust, and an object nobody can address is
            // worse than no object: it would sit in the scene, invisible to both editors' maps.
            if (!StableNodeId.IsValid(op.nodeId))
            {
                Ignored++;
                note = "invalid node id: " + op.nodeId;
                return false;
            }

            var parent = ResolveParent(op.parentNodeId);
            if (parent == null && !string.IsNullOrEmpty(op.parentNodeId))
            {
                Ignored++;
                note = "unknown parent: " + op.parentNodeId;
                return false;
            }

            AddNodePayload payload = null;
            if (!string.IsNullOrEmpty(op.newValue))
            {
                try
                {
                    payload = JsonUtility.FromJson<AddNodePayload>(op.newValue);
                }
                catch (Exception)
                {
                    payload = null;
                }
            }

            var name = payload != null && !string.IsNullOrEmpty(payload.name) ? payload.name : "Object";

            GameObject created = null;
            var prefabMissing = false;

            if (!string.IsNullOrEmpty(op.prefabGuid))
            {
                var path = AssetDatabase.GUIDToAssetPath(op.prefabGuid);
                var prefab = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null)
                {
                    created = PrefabUtility.InstantiatePrefab(prefab, parent != null ? parent.scene : gameObjectScene()) as GameObject;
                }
                else
                {
                    prefabMissing = true;
                }
            }

            if (created == null)
            {
                created = new GameObject(name);
            }

            if (created == null)
            {
                Ignored++;
                note = "could not create node";
                return false;
            }

            if (parent != null)
            {
                created.transform.SetParent(parent.transform, false);
            }
            else
            {
                SceneManager.MoveGameObjectToScene(created, gameObjectScene());
            }

            if (!string.IsNullOrEmpty(name))
            {
                created.name = name;
            }

            if (payload != null)
            {
                created.transform.localPosition = new Vector3(payload.px, payload.py, payload.pz);
                created.transform.localRotation = new Quaternion(payload.rx, payload.ry, payload.rz, payload.rw);
                created.transform.localScale = new Vector3(payload.sx, payload.sy, payload.sz);
                created.SetActive(payload.active);
            }

            if (op.siblingIndex > 0 && op.siblingIndex < created.transform.parent.childCount)
            {
                created.transform.SetSiblingIndex(op.siblingIndex);
            }

            if (prefabMissing)
            {
                // The stand-in is what the host's object will look like to this editor until the prefab
                // asset arrives: a labelled placeholder rather than nothing at all.
                var placeholder = CollabPlaceholders.Attach(created, name + " (missing prefab)");
                if (placeholder != null)
                {
                    _touched.Add(placeholder);
                }
            }

            _registry.Adopt(created, op.nodeId);
            _structure.Baseline(created, op.nodeId);
            _diff.Baseline(created, op.nodeId);
            _touched.Add(created);

            MarkDirty(created.scene);
            Applied++;
            return true;
        }

        bool ApplyRemoveNode(WireMessage op, out string note)
        {
            note = null;

            GameObject gameObject;
            if (!_registry.TryResolve(op.nodeId, out gameObject) || gameObject == null)
            {
                Ignored++;
                note = "unknown node: " + op.nodeId;
                return false;
            }

            var scene = gameObject.scene;
            _structure.Forget(op.nodeId);
            _diff.Forget(op.nodeId);
            _registry.Forget(gameObject);

            UnityEngine.Object.DestroyImmediate(gameObject);

            MarkDirty(scene);
            Applied++;
            return true;
        }

        bool ApplyReparent(WireMessage op, out string note)
        {
            note = null;

            GameObject gameObject;
            if (!_registry.TryResolve(op.nodeId, out gameObject) || gameObject == null)
            {
                Ignored++;
                note = "unknown node: " + op.nodeId;
                return false;
            }

            var parentId = op.parentNodeId ?? op.newValue ?? "";
            var parent = ResolveParent(parentId);
            if (parent == null && !string.IsNullOrEmpty(parentId))
            {
                Ignored++;
                note = "unknown parent: " + parentId;
                return false;
            }

            var previousParent = gameObject.transform.parent;
            gameObject.transform.SetParent(parent != null ? parent.transform : null, true);

            if (op.siblingIndex >= 0 && gameObject.transform.parent != null
                && op.siblingIndex < gameObject.transform.parent.childCount)
            {
                gameObject.transform.SetSiblingIndex(op.siblingIndex);
            }

            if (previousParent != null && previousParent != gameObject.transform.parent)
            {
                _structure.Baseline(previousParent.gameObject, ResolveNodeId(previousParent.gameObject));
            }

            _structure.Baseline(gameObject, op.nodeId);
            MarkDirty(gameObject.scene);
            Applied++;
            return true;
        }

        bool ApplyReorder(WireMessage op, out string note)
        {
            note = null;

            GameObject gameObject;
            if (!_registry.TryResolve(op.nodeId, out gameObject) || gameObject == null)
            {
                Ignored++;
                note = "unknown node: " + op.nodeId;
                return false;
            }

            var parent = gameObject.transform.parent;
            if (parent == null)
            {
                Ignored++;
                note = "reorder on a root object";
                return false;
            }

            if (op.siblingIndex < 0 || op.siblingIndex >= parent.childCount)
            {
                Ignored++;
                note = "sibling index out of range: " + op.siblingIndex;
                return false;
            }

            gameObject.transform.SetSiblingIndex(op.siblingIndex);
            _structure.Baseline(gameObject, op.nodeId);
            MarkDirty(gameObject.scene);
            Applied++;
            return true;
        }

        bool ApplyAddComponent(WireMessage op, out string note)
        {
            note = null;

            GameObject gameObject;
            if (!_registry.TryResolve(op.nodeId, out gameObject) || gameObject == null)
            {
                Ignored++;
                note = "unknown node: " + op.nodeId;
                return false;
            }

            var typeName = string.IsNullOrEmpty(op.componentType) ? op.newValue : op.componentType;
            var type = SceneComponentFields.ResolveType(typeName);
            if (type == null)
            {
                // The type simply is not in this project: that is the missing-script case, and it is
                // reported by the asset health layer rather than faked here.
                Ignored++;
                note = "component type not found: " + typeName;
                return false;
            }

            if (gameObject.GetComponent(type) != null)
            {
                Applied++;
                note = "component already present";
                return true;
            }

            gameObject.AddComponent(type);
            _diff.Baseline(gameObject, op.nodeId);
            MarkDirty(gameObject.scene);
            Applied++;
            return true;
        }

        GameObject ResolveParent(string parentNodeId)
        {
            if (string.IsNullOrEmpty(parentNodeId))
            {
                return null;
            }

            GameObject parent;
            _registry.TryResolve(parentNodeId, out parent);
            return parent;
        }

        string ResolveNodeId(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return null;
            }

            string nodeId;
            return _registry.TryGetNodeId(gameObject, out nodeId) ? nodeId : null;
        }

        Scene gameObjectScene()
        {
            return EditorSceneManager.GetActiveScene();
        }

        static void MarkDirty(Scene scene)
        {
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }
        }
    }
}
