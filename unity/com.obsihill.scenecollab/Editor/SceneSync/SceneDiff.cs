using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Turns "this object changed" into "here are the ops to send", by comparing current values
    /// against the last values this editor knows about.
    ///
    /// The snapshot is the whole trick. It plays two roles:
    /// 1. <c>oldValue</c> — every op must state what it replaced.
    /// 2. The echo guard — when a remote op is applied, the applier writes the new value into the
    ///    snapshot, so the next diff sees no difference and does not send the change straight back.
    ///    A boolean "applying" flag would be a race waiting to happen; a value comparison is not.
    /// </summary>
    public sealed class SceneDiff
    {
        readonly Dictionary<string, Dictionary<string, string>> _snapshot =
            new Dictionary<string, Dictionary<string, string>>();

        readonly List<string> _paths = new List<string>();

        /// <summary>
        /// The node registry, for reference fields: a reference to another scene object travels as that
        /// object's node id, and only the registry knows the id. Assigned by the session; null is safe
        /// (references then read as <c>ref:local</c> and are reported, never silently dropped).
        /// </summary>
        public SceneNodeRegistry Registry { get; set; }

        public int TrackedNodes
        {
            get { return _snapshot.Count; }
        }

        /// <summary>Records the current state of an object without emitting anything (initial sync).</summary>
        public void Baseline(GameObject gameObject, string nodeId)
        {
            if (gameObject == null || string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            var values = GetOrCreate(nodeId);
            GatherPaths(gameObject);

            for (var i = 0; i < _paths.Count; i++)
            {
                var path = _paths[i];
                string value;
                if (ScenePropertyAccess.TryRead(gameObject, path, Registry, out value))
                {
                    values[path] = value;
                }
            }
        }

        /// <summary>
        /// Compares an object against the snapshot and returns one op per changed property. Updates
        /// the snapshot as it goes, so a second call with no further edits returns nothing.
        /// </summary>
        public List<SceneOperation> Collect(GameObject gameObject, string nodeId)
        {
            var operations = new List<SceneOperation>();
            CollectInto(gameObject, nodeId, operations);
            return operations;
        }

        /// <summary>
        /// The same diff, writing into a caller-owned list.
        ///
        /// A 5 000-object sweep would otherwise allocate 5 000 lists per tick — garbage that surfaces as a
        /// periodic hitch and gets blamed on the network. The hot path reuses one list.
        /// </summary>
        public void CollectInto(GameObject gameObject, string nodeId, List<SceneOperation> operations)
        {
            operations.Clear();

            if (gameObject == null || string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            var values = GetOrCreate(nodeId);
            GatherPaths(gameObject);

            for (var i = 0; i < _paths.Count; i++)
            {
                var path = _paths[i];

                string current;
                if (!ScenePropertyAccess.TryRead(gameObject, path, Registry, out current))
                {
                    continue;
                }

                string previous;
                if (!values.TryGetValue(path, out previous))
                {
                    // First time this object is seen: adopt its value as the baseline. Emitting an op
                    // here would claim an edit nobody made.
                    values[path] = current;
                    continue;
                }

                if (SceneValueCodec.AreEqual(previous, current))
                {
                    continue;
                }

                SceneOperation operation;
                string error;
                if (SceneOperation.TryCreate(
                        KindFor(gameObject), nodeId, path, previous, current, out operation, out error))
                {
                    operations.Add(operation);
                }

                // Advance the snapshot even when the op was refused: the value is what the object now
                // holds, and pretending otherwise would resend it forever.
                values[path] = current;
            }
        }

        /// <summary>
        /// Every tracked field of the object, emitted unconditionally.
        ///
        /// Used right after a component was added: the receiver has a fresh component holding its type
        /// defaults, and the incremental diff would adopt those defaults as its baseline and never send
        /// the sender's real values. A new component therefore ships its whole field set.
        /// </summary>
        public List<SceneOperation> CollectAll(GameObject gameObject, string nodeId)
        {
            var operations = new List<SceneOperation>();
            if (gameObject == null || string.IsNullOrEmpty(nodeId))
            {
                return operations;
            }

            var values = GetOrCreate(nodeId);
            GatherPaths(gameObject);

            for (var i = 0; i < _paths.Count; i++)
            {
                var path = _paths[i];
                string current;
                if (!ScenePropertyAccess.TryRead(gameObject, path, Registry, out current))
                {
                    continue;
                }

                string previous;
                values.TryGetValue(path, out previous);

                SceneOperation operation;
                string error;
                if (SceneOperation.TryCreate(KindFor(gameObject), nodeId, path, previous ?? "", current,
                        out operation, out error))
                {
                    operations.Add(operation);
                }

                values[path] = current;
            }

            return operations;
        }

        /// <summary>
        /// The op kind a property change on this object must travel as.
        ///
        /// A value written on a prefab instance is not an edit to the instance until Unity records it as
        /// a modification; writing through SerializedProperty alone can land in memory and be dropped at
        /// save time. <c>set_prefab_override</c> is the kind whose apply path calls
        /// <c>RecordPrefabInstancePropertyModifications</c>, so anything inside a prefab instance goes
        /// that way — including Transform, whose override Unity would otherwise have to infer.
        /// </summary>
        public static string KindFor(GameObject gameObject)
        {
            return gameObject != null && PrefabUtility.IsPartOfPrefabInstance(gameObject)
                ? OpKinds.SetPrefabOverride
                : OpKinds.SetProperty;
        }

        /// <summary>
        /// Records a value that arrived from the server, so the next diff does not echo it back.
        /// </summary>
        public void NoteRemoteValue(string nodeId, string propertyPath, string value)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(propertyPath))
            {
                return;
            }

            GetOrCreate(nodeId)[propertyPath] = value ?? "";
        }

        public string GetKnownValue(string nodeId, string propertyPath)
        {
            Dictionary<string, string> values;
            string value;
            if (_snapshot.TryGetValue(nodeId, out values) && values.TryGetValue(propertyPath, out value))
            {
                return value;
            }

            return null;
        }

        public void Forget(string nodeId)
        {
            if (!string.IsNullOrEmpty(nodeId))
            {
                _snapshot.Remove(nodeId);
            }
        }

        public void Clear()
        {
            _snapshot.Clear();
        }

        /// <summary>Every path we compare for one object: the built-ins plus its component fields.</summary>
        void GatherPaths(GameObject gameObject)
        {
            _paths.Clear();
            for (var i = 0; i < ScenePropertyAccess.TrackedPaths.Length; i++)
            {
                _paths.Add(ScenePropertyAccess.TrackedPaths[i]);
            }

            SceneComponentFields.CollectPaths(gameObject, _paths);
        }

        Dictionary<string, string> GetOrCreate(string nodeId)
        {
            Dictionary<string, string> values;
            if (!_snapshot.TryGetValue(nodeId, out values))
            {
                values = new Dictionary<string, string>();
                _snapshot[nodeId] = values;
            }

            return values;
        }
    }
}
