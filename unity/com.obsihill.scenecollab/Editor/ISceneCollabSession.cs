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

        void Connect(CollabSettings settings);

        void Disconnect();

        /// <summary>
        /// Sends an op. Returns false when not connected — callers must treat that as "this edit
        /// did not reach anyone", not as a retryable hiccup.
        /// </summary>
        bool SendOperation(SceneCollab.Domain.SceneOperation operation);
    }
}
