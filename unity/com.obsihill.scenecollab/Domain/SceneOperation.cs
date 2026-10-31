using System;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Domain
{
    /// <summary>
    /// One edit expressed in terms the server understands, validated before it leaves the
    /// editor.
    ///
    /// Every op carries <c>OldValue</c>. That is the whole conflict-detection story: the
    /// server compares it against its authoritative document and rejects the op when they
    /// differ, instead of silently overwriting someone else's work. An op without a
    /// trustworthy old value is worse than no op at all, so <see cref="TryCreate"/> refuses.
    /// </summary>
    [Serializable]
    public struct SceneOperation
    {
        public string Kind;
        public string NodeId;
        public string PropertyPath;
        public string OldValue;
        public string NewValue;

        /// <summary>
        /// Builds an op, or explains why it cannot be one. Failure here is a caller bug, not
        /// a user error, so the message names the field.
        /// </summary>
        public static bool TryCreate(
            string kind,
            string nodeId,
            string propertyPath,
            string oldValue,
            string newValue,
            out SceneOperation operation,
            out string error)
        {
            operation = default(SceneOperation);
            error = null;

            if (string.IsNullOrEmpty(kind))
            {
                error = "op kind is required";
                return false;
            }

            if (!StableNodeId.IsValid(nodeId))
            {
                error = "node id is missing or malformed: " + (nodeId ?? "<null>");
                return false;
            }

            var needsProperty = kind == OpKinds.SetProperty || kind == OpKinds.SetPrefabOverride
                || kind == OpKinds.AddComponent;
            if (needsProperty && string.IsNullOrEmpty(propertyPath))
            {
                error = kind + " requires a property path";
                return false;
            }

            // Add uses null/absent old values by definition; everything else must state what
            // it overwrote or the server cannot reject a stale op.
            var needsOldValue = kind != OpKinds.AddNode;
            if (needsOldValue && oldValue == null)
            {
                error = kind + " requires the previous value";
                return false;
            }

            if (newValue == null && kind != OpKinds.RemoveNode)
            {
                error = kind + " requires the new value";
                return false;
            }

            operation = new SceneOperation
            {
                Kind = kind,
                NodeId = nodeId,
                PropertyPath = propertyPath,
                OldValue = oldValue,
                NewValue = newValue,
            };

            return true;
        }

        public WireMessage ToWire(string clientId, string room, long revision, string opId)
        {
            return new WireMessage
            {
                type = MessageTypes.Op,
                protocolVersion = ProtocolVersion.Current,
                clientId = clientId,
                room = room,
                revision = revision,
                opId = opId,
                opKind = Kind,
                nodeId = NodeId,
                propertyPath = PropertyPath,
                oldValue = OldValue,
                newValue = NewValue,
            };
        }

        public override string ToString()
        {
            return Kind + "(" + NodeId + (string.IsNullOrEmpty(PropertyPath) ? "" : "." + PropertyPath) + ")";
        }
    }
}
