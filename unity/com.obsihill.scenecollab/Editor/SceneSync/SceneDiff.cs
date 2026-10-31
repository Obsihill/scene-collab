using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
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
            for (var i = 0; i < ScenePropertyAccess.TrackedPaths.Length; i++)
            {
                var path = ScenePropertyAccess.TrackedPaths[i];
                string value;
                if (ScenePropertyAccess.TryRead(gameObject, path, out value))
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

            if (gameObject == null || string.IsNullOrEmpty(nodeId))
            {
                return operations;
            }

            var values = GetOrCreate(nodeId);

            for (var i = 0; i < ScenePropertyAccess.TrackedPaths.Length; i++)
            {
                var path = ScenePropertyAccess.TrackedPaths[i];

                string current;
                if (!ScenePropertyAccess.TryRead(gameObject, path, out current))
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
                        OpKinds.SetProperty, nodeId, path, previous, current, out operation, out error))
                {
                    operations.Add(operation);
                }

                // Advance the snapshot even when the op was refused: the value is what the object now
                // holds, and pretending otherwise would resend it forever.
                values[path] = current;
            }

            return operations;
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
