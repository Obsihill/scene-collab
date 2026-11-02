using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Editor
{
    /// <summary>Connection lifecycle, as the window needs to render it.</summary>
    public enum CollabConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Faulted,
    }

    /// <summary>
    /// One live connection to a collaboration room. This is the only surface the rest of the
    /// editor (window, and later the scene capture/apply layer) is allowed to depend on.
    ///
    /// Events fire on the Unity main thread, from <c>EditorApplication.update</c>. A socket
    /// callback touching a Unity API would be a crash that only reproduces under load.
    /// </summary>
    public interface ISceneCollabSession
    {
        CollabConnectionState State { get; }

        /// <summary>Server-assigned id for this editor. Empty until the welcome arrives.</summary>
        string ClientId { get; }

        /// <summary>True for the one participant allowed to write the scene into Git.</summary>
        bool IsScribe { get; }

        /// <summary>
        /// True when this editor opened the scene and therefore owns it: it is the only one allowed to
        /// save, and the room is bound to its scene.
        /// </summary>
        bool IsHost { get; }

        /// <summary>Guid of the scene this room edits, as announced by the server.</summary>
        string SceneGuid { get; }

        string ScenePath { get; }

        string SceneName { get; }

        /// <summary>Node ids that are currently locked by a participant.</summary>
        string[] LockedNodeIds { get; }

        /// <summary>Asset guids the host's scene depends on; empty until the host publishes them.</summary>
        string[] AssetGuids { get; }

        /// <summary>Number of assets in the host's manifest (0 before the first publish).</summary>
        int AssetCount { get; }

        bool IsLockedByOther(string nodeId);

        /// <summary>Last revision this editor has applied.</summary>
        long Revision { get; }

        string RoomName { get; }

        IReadOnlyList<ParticipantInfo> Participants { get; }

        event Action StateChanged;

        event Action ParticipantsChanged;

        /// <summary>An op from someone else. Not yet applied — applying is the scene layer's job.</summary>
        event Action<WireMessage> OperationReceived;

        /// <summary>
        /// The server's verdict on an op this editor sent (<c>ack</c>): accepted, rejected as stale,
        /// or a duplicate replay. The scene layer uses it to pace the outbox and to resync on refusal.
        /// </summary>
        event Action<WireMessage> OperationAcknowledged;

        /// <summary>Rejection or protocol error: code from <see cref="ErrorCodes"/>, human text.</summary>
        event Action<string, string> ErrorReceived;

        /// <summary>The room's lock set changed (someone locked, unlocked, or left).</summary>
        event Action LocksChanged;

        /// <summary>The host published a new asset manifest.</summary>
        event Action AssetManifestChanged;

        void Connect(CollabSettings settings);

        void Disconnect();

        /// <summary>
        /// Sends an op. Returns false when not connected — callers must treat that as "this edit
        /// did not reach anyone", not as a retryable hiccup.
        /// </summary>
        bool SendOperation(SceneCollab.Domain.SceneOperation operation);

        /// <summary>Requests or releases the edit lock on a node.</summary>
        bool SendLock(string nodeId, bool release);

        /// <summary>
        /// Sends one frame of a host snapshot (v0.4.3). Frames are ops on the wire, so they are ordered
        /// with edits and rejected when stale; callers must pace them one acknowledgement at a time.
        /// </summary>
        bool SendSnapshotFrame(WireMessage frame);

        /// <summary>
        /// Broadcasts this editor's scene-view camera pose (v0.4.3). Presence only: never applied to the
        /// scene, never advances the revision.
        /// </summary>
        bool SendView(float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW, float fov);

        /// <summary>A peer's scene-view camera pose (v0.4.3), for the scene-view overlay.</summary>
        event Action<WireMessage> ViewReceived;

        /// <summary>
        /// Sends one rectangle of terrain samples (v0.5). Terrain data lives in a TerrainData asset,
        /// not in the scene, so it rides its own channel instead of the op log.
        /// </summary>
        bool SendTerrain(WireMessage patch);

        /// <summary>A terrain patch from the host (v0.5).</summary>
        event Action<WireMessage> TerrainReceived;

        /// <summary>Tells the room how many assets the host's scene needs (host only).</summary>
        bool PublishAssetCount(int count);

        /// <summary>Publishes the host's full asset manifest so peers can flag what they lack.</summary>
        bool PublishAssets(string[] assetGuids);
    }
}
