namespace SceneCollab.Domain.Room
{
    /// <summary>
    /// An accepted operation, kept for replay and for re-serving a client that fell behind.
    /// Server-side only: it is never serialized to the wire as-is.
    /// </summary>
    public sealed class OpRecord
    {
        public string OpId;
        public string ClientId;
        public string Kind;
        public string NodeId;
        public string PropertyPath;
        public string OldValue;
        public string NewValue;

        /// <summary>Revision assigned by the server (1-based, strictly increasing).</summary>
        public long Revision;

        public long TimestampUnixMs;
    }
}
