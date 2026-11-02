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

        /// <summary>Client asks for (or releases) the edit lock on a node; the server echoes the full lock set.</summary>
        public const string Lock = "lock";

        public const string Unlock = "unlock";

        /// <summary>Host publishes the asset GUIDs its copy of the scene depends on.</summary>
        public const string Assets = "assets";

        /// <summary>
        /// Peer scene-view camera state (v0.4.3). Broadcast to the other participants and never
        /// to the op history: looking around is not an edit, so it must not advance the revision.
        /// </summary>
        public const string View = "view";

        /// <summary>
        /// Rectangle of terrain samples (v0.5). Broadcast like <c>view</c> — outside the op log,
        /// because terrain data is an asset, not a scene node, and one sculpt stroke can be dozens of
        /// tiles that must not each cost a revision.
        /// </summary>
        public const string Terrain = "terrain";
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

        /// <summary>The room is bound to a different scene than the one this client has open.</summary>
        public const string SceneMismatch = "scene_mismatch";

        /// <summary>Another participant holds the edit lock on this node.</summary>
        public const string Locked = "locked";

        /// <summary>Only the host may perform this action (saving, publishing the asset manifest).</summary>
        public const string NotHost = "not_host";
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
        public const string RemoveComponent = "remove_component";
        public const string SetProperty = "set_property";
        public const string SetPrefabOverride = "set_prefab_override";

        // ── v0.4.3: host → guest baseline ────────────────────────────────────────
        //
        // A guest that joins later has no history: the server keeps the last N ops, but a scene
        // that was built up over an hour is not in them. The host therefore pushes its whole
        // scene as three ops. They are ops (not a side channel) so they ride the same ordered,
        // revision-checked path as an edit, and a guest that ignores them is simply stale.

        /// <summary>Opens a snapshot; carries the host's revision at build time in <c>newValue</c>.</summary>
        public const string SnapshotStart = "snapshot_start";

        /// <summary>One chunk of the snapshot; <c>newValue</c> is the JSON of a <c>SnapshotChunk</c>.</summary>
        public const string SnapshotChunk = "snapshot_chunk";

        /// <summary>Closes a snapshot; <c>newValue</c> is the number of nodes it carried.</summary>
        public const string SnapshotEnd = "snapshot_end";

        /// <summary>
        /// Guest → host: "I just started syncing and have nothing." The host answers with a snapshot.
        /// Sent once per enable, so a room where everyone already has the scene stays quiet.
        /// </summary>
        public const string SnapshotRequest = "snapshot_request";

        public static bool IsSnapshot(string kind)
        {
            return kind == SnapshotStart || kind == SnapshotChunk || kind == SnapshotEnd;
        }

        public static bool IsStructural(string kind)
        {
            return kind == AddNode || kind == RemoveNode || kind == Reparent || kind == ReorderChildren
                || kind == AddComponent || kind == RemoveComponent;
        }
    }
}
