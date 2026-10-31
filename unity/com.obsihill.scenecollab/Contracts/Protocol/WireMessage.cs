using System;

namespace SceneCollab.Contracts.Protocol
{
    /// <summary>
    /// The single message shape used on the wire, in both directions.
    ///
    /// Deliberately FLAT: public fields, no dictionaries, no polymorphism, no
    /// inheritance. Three reasons, none of them aesthetic:
    /// 1. Unity's built-in <c>JsonUtility</c> handles exactly this shape, so the editor
    ///    package needs zero serialization dependencies (no NuGet, no IL2CPP quirks).
    /// 2. Polymorphic deserialization of untrusted input is a known RCE class; a flat
    ///    DTO cannot express it.
    /// 3. One class = one schema = one place to add a field as the protocol grows.
    ///
    /// Consequence: unused fields are omitted on send rather than set to null, and every
    /// field name here is part of the public protocol (renaming = breaking change).
    /// </summary>
    [Serializable]
    public sealed class WireMessage
    {
        /// <summary>One of <see cref="MessageTypes"/>.</summary>
        public string type;

        /// <summary>Protocol version; mandatory on <c>hello</c>.</summary>
        public int protocolVersion;

        /// <summary>Access token. Only sent on <c>hello</c> when the connection could not use the query string.</summary>
        public string token;

        /// <summary>Client identity assigned by the server (server→client).</summary>
        public string clientId;

        /// <summary>Human readable name shown in the presence list.</summary>
        public string displayName;

        /// <summary>Presence colour, hex letter ("#RRGGBB").</summary>
        public string color;

        /// <summary>Room name; a single server can host several isolated rooms.</summary>
        public string room;

        /// <summary>Authoritative revision the sender had applied when it produced the op.</summary>
        public long revision;

        /// <summary>Client-generated idempotency key for operations.</summary>
        public string opId;

        /// <summary>Stable node id (GlobalObjectId hash). Never a fileID.</summary>
        public string nodeId;

        /// <summary>Property path, e.g. <c>m_LocalPosition.x</c>.</summary>
        public string propertyPath;

        /// <summary>Value before the edit, serialized as JSON. Required for conflict detection and undo/redo replay.</summary>
        public string oldValue;

        /// <summary>Value after the edit, serialized as JSON.</summary>
        public string newValue;

        /// <summary>One of <see cref="OpKinds"/>.</summary>
        public string opKind;

        /// <summary>Result of an <c>op</c> (<c>ack</c>).</summary>
        public bool accepted;

        /// <summary>Machine-readable failure code, one of <see cref="ErrorCodes"/>.</summary>
        public string code;

        /// <summary>Human readable detail. Never surfaced verbatim in the editor UI without a code.</summary>
        public string message;

        /// <summary>Participant list (server→client on welcome/presence).</summary>
        public ParticipantInfo[] participants;

        /// <summary>Node ids the sending client has selected (client→server on presence).</summary>
        public string[] selectedNodeIds;

        /// <summary>True when the receiving client is the scribe (the only client allowed to save the scene).</summary>
        public bool isScribe;

        /// <summary>Server clock, unix ms. Used to detect clock skew in diagnostics.</summary>
        public long serverTime;

        /// <summary>True when an op was accepted but had already been applied (idempotent replay).</summary>
        public bool duplicate;

        // ── v0.3: scene identity, host role, locks, structural payloads ──────────

        /// <summary>
        /// GUID of the scene being edited. Sent on <c>hello</c>; the server binds the room to the
        /// first scene it sees and refuses anyone editing a different one. Without this a client that
        /// opened the wrong scene joined silently and its ops were dropped node by node.
        /// </summary>
        public string sceneGuid;

        /// <summary>Project-relative scene path, for the room list and error messages.</summary>
        public string scenePath;

        /// <summary>Scene asset name without extension, shown in the room list.</summary>
        public string sceneName;

        /// <summary>True for the participant that opened the scene and owns saving (the host).</summary>
        public bool isHost;

        /// <summary>Node a <c>lock</c>/<c>unlock</c> message refers to.</summary>
        public string lockNodeId;

        /// <summary>Every node currently locked in the room, broadcast with the lock change.</summary>
        public string[] lockedNodeIds;

        /// <summary>Parent node for a structural op; empty string means the scene root.</summary>
        public string parentNodeId;

        /// <summary>Sibling index inside the parent, used by <c>add_node</c> and <c>reorder_children</c>.</summary>
        public int siblingIndex;

        /// <summary>Component type name for <c>add_component</c>.</summary>
        public string componentType;

        /// <summary>
        /// Asset GUID a created object was made from (prefab, model). Empty for a plain GameObject.
        /// GUIDs, never paths: a path is machine-specific and a missing asset must be detectable.
        /// </summary>
        public string prefabGuid;

        /// <summary>Asset GUIDs referenced by the host's copy of the scene (asset manifest).</summary>
        public string[] assetGuids;

        /// <summary>Number of assets the host's scene depends on; sent with the manifest.</summary>
        public int assetCount;

        // ── v0.4.3: peer scene-view presence ─────────────────────────────────────

        /// <summary>
        /// True when a <c>view</c> message carries a usable camera pose. A peer that has no scene
        /// view open (or a stale sender) leaves it false and is not drawn.
        /// </summary>
        public bool viewValid;

        /// <summary>Scene-view camera world position (editor-only overlay; never part of the scene).</summary>
        public float viewPosX;
        public float viewPosY;
        public float viewPosZ;

        /// <summary>Scene-view camera world rotation as a quaternion (x, y, z, w).</summary>
        public float viewRotX;
        public float viewRotY;
        public float viewRotZ;
        public float viewRotW;

        /// <summary>Scene-view camera vertical field of view in degrees, for the frustum gizmo.</summary>
        public float viewFov;

        /// <summary>
        /// True when the op is part of a host snapshot rather than a live edit. The applier uses it
        /// only to keep the "someone overwrote your value" counter honest: a baseline is expected to
        /// replace values and must not read as a conflict.
        /// </summary>
        public bool snapshot;
    }

    /// <summary>
    /// Presence entry. Also flat and JsonUtility-friendly.
    /// </summary>
    [Serializable]
    public sealed class ParticipantInfo
    {
        public string clientId;
        public string displayName;
        public string color;
        public bool isScribe;

        /// <summary>True for the participant that owns the scene and is the only one allowed to save.</summary>
        public bool isHost;

        /// <summary>Node ids the participant currently has selected. Used for the "someone else is here" highlight.</summary>
        public string[] selectedNodeIds;
    }
}
