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

        /// <summary>Node ids the participant currently has selected. Used for the "someone else is here" highlight.</summary>
        public string[] selectedNodeIds;
    }
}
