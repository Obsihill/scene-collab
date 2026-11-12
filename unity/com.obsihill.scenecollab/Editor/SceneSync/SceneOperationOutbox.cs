using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>Coalesces only adjacent, causally continuous property writes that have not been sent.</summary>
    internal sealed class SceneOperationOutbox
    {
        readonly Queue<SceneOperation> _queue = new Queue<SceneOperation>();
        SceneOperation _tail;
        bool _hasTail;

        public int Count { get { return _queue.Count + (_hasTail ? 1 : 0); } }

        public void Enqueue(SceneOperation operation)
        {
            if (_hasTail && CanMerge(_tail, operation))
            {
                _tail.NewValue = operation.NewValue;
                if (string.Equals(_tail.OldValue, _tail.NewValue, StringComparison.Ordinal))
                {
                    _hasTail = false;
                    _tail = default(SceneOperation);
                }
                return;
            }
            if (_hasTail) _queue.Enqueue(_tail);
            _tail = operation;
            _hasTail = true;
        }

        public SceneOperation Dequeue()
        {
            if (_queue.Count > 0) return _queue.Dequeue();
            if (!_hasTail) throw new InvalidOperationException("The outbox is empty.");
            _hasTail = false;
            var result = _tail;
            _tail = default(SceneOperation);
            return result;
        }

        public void Clear()
        {
            _queue.Clear();
            _hasTail = false;
            _tail = default(SceneOperation);
        }

        static bool CanMerge(SceneOperation first, SceneOperation next)
        {
            return (first.Kind == OpKinds.SetProperty || first.Kind == OpKinds.SetPrefabOverride)
                && first.Kind == next.Kind && first.NodeId == next.NodeId
                && first.PropertyPath == next.PropertyPath
                && string.Equals(first.NewValue, next.OldValue, StringComparison.Ordinal);
        }
    }
}
