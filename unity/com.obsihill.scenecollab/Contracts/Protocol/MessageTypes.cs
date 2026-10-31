namespace SceneCollab.Contracts.Protocol
{
    /// <summary>Values for <see cref="WireMessage.type"/>.</summary>
    public static class MessageTypes
    {
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string Presence = "presence";
        public const string Op = "op";
        public const string Ack = "ack";
        public const string Error = "error";
        public const string Ping = "ping";
        public const string Pong = "pong";
    }

    /// <summary>Values for <see cref="WireMessage.code"/>.</summary>
    public static class ErrorCodes
    {
        public const string BadToken = "bad_token";
        public const string ProtocolMismatch = "protocol_mismatch";
        public const string StaleRevision = "stale_revision";
        public const string NotInitialized = "not_initialized";
        public const string Malformed = "malformed";
        public const string RoomFull = "room_full";
    }

    /// <summary>
    /// Operation kinds. Structural kinds (Add/Remove/Reparent/Reorder) require the
    /// object lock in the editor; property kinds fall back to last-write-wins when the
    /// revision matches.
    /// </summary>
    public static class OpKinds
    {
        public const string AddNode = "add_node";
        public const string RemoveNode = "remove_node";
        public const string Reparent = "reparent";
        public const string ReorderChildren = "reorder_children";
        public const string AddComponent = "add_component";
        public const string SetProperty = "set_property";
        public const string SetPrefabOverride = "set_prefab_override";

        public static bool IsStructural(string kind)
        {
            return kind == AddNode || kind == RemoveNode || kind == Reparent || kind == ReorderChildren
                || kind == AddComponent;
        }
    }
}
