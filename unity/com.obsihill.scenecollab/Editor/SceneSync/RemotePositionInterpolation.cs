using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>Temporarily displays remote local positions between authoritative updates.</summary>
    internal sealed class RemotePositionInterpolation
    {
        internal const double DurationSeconds = 0.1;

        sealed class Movement
        {
            public Transform Transform;
            public Vector3 From;
            public Vector3 Target;
            public Vector3 LastApplied;
            public double Started;
        }

        readonly Dictionary<string, Movement> _active = new Dictionary<string, Movement>(StringComparer.Ordinal);
        readonly List<string> _finished = new List<string>();
        double _nextFrame;

        public bool IsAnimating(string nodeId)
        {
            Movement movement;
            if (nodeId == null || !_active.TryGetValue(nodeId, out movement)) return false;
            if (movement.Transform != null && movement.Transform.localPosition.Equals(movement.LastApplied)) return true;
            _active.Remove(nodeId);
            return false;
        }

        // Called after the normal serialized write has established the authoritative target and baseline.
        public void Start(string nodeId, Transform transform, Vector3 from, double now)
        {
            if (string.IsNullOrEmpty(nodeId) || transform == null) return;
            var target = transform.localPosition;
            if (from.Equals(target))
            {
                _active.Remove(nodeId);
                return;
            }

            Movement movement;
            if (!_active.TryGetValue(nodeId, out movement))
            {
                movement = new Movement();
                _active.Add(nodeId, movement);
            }
            movement.Transform = transform;
            movement.From = from;
            movement.Target = target;
            movement.LastApplied = from;
            movement.Started = now;
            transform.localPosition = from;
        }

        public void Tick(double now)
        {
            if (_active.Count == 0 || now < _nextFrame) return;
            _nextFrame = now + 1.0 / 60.0;
            _finished.Clear();
            var changed = false;
            foreach (var pair in _active)
            {
                var movement = pair.Value;
                var transform = movement.Transform;
                if (transform == null || !transform.localPosition.Equals(movement.LastApplied))
                {
                    // A local edit wins over the visual transition, and is picked up by the normal diff.
                    _finished.Add(pair.Key);
                    continue;
                }

                var t = Mathf.Clamp01((float)((now - movement.Started) / DurationSeconds));
                movement.LastApplied = Vector3.Lerp(movement.From, movement.Target, t);
                transform.localPosition = movement.LastApplied;
                changed = true;
                if (t >= 1f)
                {
                    RecordPrefab(transform);
                    _finished.Add(pair.Key);
                }
            }
            for (var i = 0; i < _finished.Count; i++) _active.Remove(_finished[i]);
            if (changed) SceneView.RepaintAll();
        }

        public void Cancel(string nodeId)
        {
            if (nodeId != null) _active.Remove(nodeId);
        }

        public void Finish()
        {
            foreach (var movement in _active.Values)
            {
                var transform = movement.Transform;
                if (transform == null || !transform.localPosition.Equals(movement.LastApplied)) continue;
                transform.localPosition = movement.Target;
                RecordPrefab(transform);
            }
            _active.Clear();
        }

        static void RecordPrefab(Transform transform)
        {
            if (PrefabUtility.IsPartOfPrefabInstance(transform))
                PrefabUtility.RecordPrefabInstancePropertyModifications(transform);
        }
    }
}
