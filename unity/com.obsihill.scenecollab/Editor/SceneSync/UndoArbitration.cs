using System.Collections.Generic;
using SceneCollab.Domain;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Decides which parts of a local undo may still be published to the room.
    ///
    /// Undo is the one local edit that can be plainly *wrong*. Ctrl+Z restores a value this editor
    /// wrote a minute ago, and Unity's undo stack is per editor: it knows nothing about the room, so
    /// it happily reverts a field a peer has changed since. From that peer's point of view their edit
    /// simply disappeared, with no error and no conflict marker.
    ///
    /// The rule is ownership by last writer: a field belongs to whoever wrote it last. An undo may
    /// publish fields this editor wrote last; a field a peer wrote last is left alone — the editor
    /// keeps the peer's value, which is already what is on screen — and counted, so the window can say
    /// why the undo did less than the user asked instead of failing silently.
    ///
    /// Deliberately knows nothing about Unity: this is the part worth testing, and it is testable only
    /// if it stays a dictionary and a comparison.
    /// </summary>
    public sealed class UndoArbitration
    {
        readonly Dictionary<string, string> _writers = new Dictionary<string, string>();

        /// <summary>This editor's client id. Empty means "not known yet", which allows everything.</summary>
        public string SelfId { get; set; }

        /// <summary>Fields an undo refused to publish because a peer wrote them last.</summary>
        public int Refused { get; private set; }

        /// <summary>Fields an undo published.</summary>
        public int Allowed { get; private set; }

        /// <summary>The last peer that touched a field, or null when nobody has.</summary>
        public string OwnerOf(string nodeId, string propertyPath)
        {
            string writer;
            return _writers.TryGetValue(Key(nodeId, propertyPath), out writer) ? writer : null;
        }

        /// <summary>Records who wrote a field last: this editor when it emits, the peer when one lands.</summary>
        public void NoteWrite(string nodeId, string propertyPath, string writer)
        {
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(propertyPath) || string.IsNullOrEmpty(writer))
            {
                return;
            }

            _writers[Key(nodeId, propertyPath)] = writer;
        }

        /// <summary>
        /// True when this editor may publish an undo of the field. Unknown ownership allows: refusing
        /// then would make Ctrl+Z dead for anything this editor changed before it connected.
        /// </summary>
        public bool ShouldPublish(string nodeId, string propertyPath)
        {
            var owner = OwnerOf(nodeId, propertyPath);
            var allowed = owner == null || string.IsNullOrEmpty(SelfId) || owner == SelfId;
            if (allowed)
            {
                Allowed++;
            }
            else
            {
                Refused++;
            }

            return allowed;
        }

        public bool ShouldPublish(SceneOperation operation)
        {
            return ShouldPublish(operation.NodeId, operation.PropertyPath);
        }

        /// <summary>Drops every field of a node — it was deleted, so its ownership is stale.</summary>
        public void Forget(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return;
            }

            var prefix = nodeId + "|";
            var stale = new List<string>();
            foreach (var key in _writers.Keys)
            {
                if (key.StartsWith(prefix, System.StringComparison.Ordinal))
                {
                    stale.Add(key);
                }
            }

            for (var i = 0; i < stale.Count; i++)
            {
                _writers.Remove(stale[i]);
            }
        }

        public void ResetCounters()
        {
            Refused = 0;
            Allowed = 0;
        }

        public void Clear()
        {
            _writers.Clear();
            ResetCounters();
        }

        public int TrackedFields
        {
            get { return _writers.Count; }
        }

        static string Key(string nodeId, string propertyPath)
        {
            return (nodeId ?? string.Empty) + "|" + (propertyPath ?? string.Empty);
        }
    }
}
