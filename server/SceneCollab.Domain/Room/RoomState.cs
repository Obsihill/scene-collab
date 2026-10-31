using System;
using System.Collections.Generic;
using System.Linq;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Domain.Room
{
    /// <summary>
    /// The authoritative scene document for one room.
    ///
    /// Every mutation goes through a single lock. That is the whole conflict model for
    /// v0.1: the server serialises operations, so <see cref="Revision"/> is a total
    /// order and "who wins" is never ambiguous. A client that sends an op against an
    /// outdated revision is rejected (never silently rebased) — the editor then resyncs.
    ///
    /// Why not CRDT: scene hierarchy, sibling order and prefab overrides do not have a
    /// merge function that preserves author intent, and the scribe has to write a single
    /// .unity file anyway. A total order plus an explicit resync is honest; a fake merge
    /// is not.
    /// </summary>
    public sealed class RoomState
    {
        public const int DefaultHistoryLimit = 4096;

        static readonly string[] Palette =
        {
            "#4C9AFF", "#F368E0", "#20C997", "#FF9F43", "#EE5253",
            "#54A0FF", "#00D2D3", "#FECA57", "#A29BFE", "#1DD1A1",
        };

        readonly object _gate = new object();
        readonly Dictionary<string, ParticipantInfo> _participants =
            new Dictionary<string, ParticipantInfo>(StringComparer.Ordinal);
        readonly List<OpRecord> _history = new List<OpRecord>();
        readonly Dictionary<string, OpRecord> _applied = new Dictionary<string, OpRecord>(StringComparer.Ordinal);
        readonly Dictionary<string, string> _locks = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly int _historyLimit;

        long _revision;
        string _scribeClientId;
        string _hostClientId;

        // Join order, oldest first. Promotion must land on the editor that has been in the scene
        // longest — picking by client id (which is random) handed the role to a random person.
        readonly List<string> _joinOrder = new List<string>();
        string _sceneGuid;
        string _scenePath;
        string _sceneName;

        public RoomState(int historyLimit = DefaultHistoryLimit)
        {
            if (historyLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(historyLimit));
            }

            _historyLimit = historyLimit;
        }

        public long Revision
        {
            get { lock (_gate) { return _revision; } }
        }

        public string ScribeClientId
        {
            get { lock (_gate) { return _scribeClientId; } }
        }

        /// <summary>
        /// The participant that opened the scene. It is also the scribe: the only editor allowed to
        /// write the scene file. Promoted to the next participant if the host disconnects, because a
        /// room with edits and nobody able to save is a dead session.
        /// </summary>
        public string HostClientId
        {
            get { lock (_gate) { return _hostClientId; } }
        }

        public string SceneGuid
        {
            get { lock (_gate) { return _sceneGuid; } }
        }

        public string ScenePath
        {
            get { lock (_gate) { return _scenePath; } }
        }

        public string SceneName
        {
            get { lock (_gate) { return _sceneName; } }
        }

        public string[] SnapshotLocks()
        {
            lock (_gate) { return _locks.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray(); }
        }

        /// <summary>
        /// Binds the room to a scene, or explains why it cannot. The first join wins: that is the host
        /// opening the scene, and everyone after it must have the same one. An empty guid (an old
        /// client, or a scripted peer) binds nothing and is not rejected, which keeps v0.2 peers usable.
        /// </summary>
        public bool TryBindScene(string sceneGuid, string scenePath, string sceneName, out string conflict)
        {
            conflict = null;

            lock (_gate)
            {
                if (string.IsNullOrEmpty(sceneGuid))
                {
                    return true;
                }

                if (string.IsNullOrEmpty(_sceneGuid))
                {
                    _sceneGuid = sceneGuid;
                    _scenePath = scenePath ?? "";
                    _sceneName = sceneName ?? "";
                    return true;
                }

                if (string.Equals(_sceneGuid, sceneGuid, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                conflict = "room is bound to " + (_sceneName.Length == 0 ? _sceneGuid : _sceneName)
                    + " (" + _sceneGuid + "), this editor has " + sceneGuid;
                return false;
            }
        }

        /// <summary>Takes the edit lock for a node. Fails when someone else already holds it.</summary>
        public bool TryLock(string clientId, string nodeId, out string owner)
        {
            owner = null;

            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(clientId))
            {
                return false;
            }

            lock (_gate)
            {
                string current;
                if (_locks.TryGetValue(nodeId, out current) && !string.Equals(current, clientId, StringComparison.Ordinal))
                {
                    owner = current;
                    return false;
                }

                _locks[nodeId] = clientId;
                return true;
            }
        }

        /// <summary>Releases a node lock. Only the holder can release it.</summary>
        public bool Unlock(string clientId, string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return false;
            }

            lock (_gate)
            {
                string current;
                if (!_locks.TryGetValue(nodeId, out current))
                {
                    return true;
                }

                if (!string.Equals(current, clientId, StringComparison.Ordinal))
                {
                    return false;
                }

                _locks.Remove(nodeId);
                return true;
            }
        }

        public int ParticipantCount
        {
            get { lock (_gate) { return _participants.Count; } }
        }

        public ParticipantInfo[] SnapshotParticipants()
        {
            lock (_gate)
            {
                return _participants.Values
                    .OrderBy(p => p.clientId, StringComparer.Ordinal)
                    .Select(Clone)
                    .ToArray();
            }
        }

        public OpRecord[] SnapshotHistory()
        {
            lock (_gate) { return _history.Select(Clone).ToArray(); }
        }

        public ParticipantInfo Join(string clientId, string displayName, string color)
        {
            if (string.IsNullOrEmpty(clientId))
            {
                throw new ArgumentException("clientId is required", nameof(clientId));
            }

            lock (_gate)
            {
                var participant = new ParticipantInfo
                {
                    clientId = clientId,
                    displayName = string.IsNullOrWhiteSpace(displayName) ? clientId : displayName.Trim(),
                    color = string.IsNullOrEmpty(color) ? Palette[_participants.Count % Palette.Length] : color,
                    selectedNodeIds = Array.Empty<string>(),
                };

                _participants[clientId] = participant;
                if (!_joinOrder.Contains(clientId))
                {
                    _joinOrder.Add(clientId);
                }

                // First in is the host: it is the editor that opened the scene, so it is also the one
                // allowed to save. Scribe is not a separate role any more — it follows the host.
                if (string.IsNullOrEmpty(_hostClientId) || !_participants.ContainsKey(_hostClientId))
                {
                    _hostClientId = clientId;
                }

                _scribeClientId = _hostClientId;

                ApplyScribeFlags();
                return Clone(participant);
            }
        }

        public bool Leave(string clientId)
        {
            if (string.IsNullOrEmpty(clientId))
            {
                return false;
            }

            lock (_gate)
            {
                if (!_participants.Remove(clientId))
                {
                    return false;
                }

                _joinOrder.Remove(clientId);

                if (_scribeClientId == clientId)
                {
                    _hostClientId = _joinOrder.FirstOrDefault(id => _participants.ContainsKey(id));
                    _scribeClientId = _hostClientId;
                }

                // A lock held by a participant that left would freeze the node for everybody else.
                var orphaned = _locks.Where(pair => pair.Value == clientId).Select(pair => pair.Key).ToArray();
                for (var i = 0; i < orphaned.Length; i++)
                {
                    _locks.Remove(orphaned[i]);
                }

                if (_participants.Count == 0)
                {
                    // Nobody left: the session is over, so the room may bind a different scene next time.
                    _joinOrder.Clear();
                    _hostClientId = null;
                    _sceneGuid = null;
                    _scenePath = null;
                    _sceneName = null;
                    _locks.Clear();
                }

                ApplyScribeFlags();
                return true;
            }
        }

        public bool UpdatePresence(string clientId, string[] selectedNodeIds)
        {
            lock (_gate)
            {
                ParticipantInfo participant;
                if (!_participants.TryGetValue(clientId, out participant))
                {
                    return false;
                }

                participant.selectedNodeIds = selectedNodeIds ?? Array.Empty<string>();
                return true;
            }
        }

        public ApplyResult Apply(WireMessage op)
        {
            if (op == null)
            {
                return ApplyResult.Reject(ErrorCodes.Malformed, 0, "empty message");
            }

            lock (_gate)
            {
                if (string.IsNullOrEmpty(op.opId))
                {
                    return ApplyResult.Reject(ErrorCodes.Malformed, _revision, "opId is required");
                }

                if (string.IsNullOrEmpty(op.opKind))
                {
                    return ApplyResult.Reject(ErrorCodes.Malformed, _revision, "opKind is required");
                }

                if (string.IsNullOrEmpty(op.clientId) || !_participants.ContainsKey(op.clientId))
                {
                    return ApplyResult.Reject(ErrorCodes.NotInitialized, _revision, "unknown clientId");
                }

                OpRecord previous;
                if (_applied.TryGetValue(op.opId, out previous))
                {
                    // Idempotent replay after a reconnect: acknowledge instead of applying twice.
                    return ApplyResult.AcceptDuplicate(op.opId, _revision, Clone(previous));
                }

                if (op.revision != _revision)
                {
                    return ApplyResult.Reject(
                        ErrorCodes.StaleRevision,
                        _revision,
                        "op was produced against revision " + op.revision);
                }

                // Object lock: a node being restructured by one editor is off limits to the others.
                // Rejecting (rather than applying and hoping) is what makes hierarchy edits predictable.
                string lockOwner;
                if (!string.IsNullOrEmpty(op.nodeId) && _locks.TryGetValue(op.nodeId, out lockOwner)
                    && !string.Equals(lockOwner, op.clientId, StringComparison.Ordinal))
                {
                    return ApplyResult.Reject(ErrorCodes.Locked, _revision, "node is locked by " + lockOwner);
                }

                _revision++;

                var record = new OpRecord
                {
                    OpId = op.opId,
                    ClientId = op.clientId,
                    Kind = op.opKind,
                    NodeId = op.nodeId,
                    PropertyPath = op.propertyPath,
                    OldValue = op.oldValue,
                    NewValue = op.newValue,
                    Revision = _revision,
                    TimestampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };

                _history.Add(record);
                _applied[op.opId] = record;
                TrimHistory();

                return ApplyResult.Accept(Clone(record));
            }
        }

        void TrimHistory()
        {
            while (_history.Count > _historyLimit)
            {
                var removed = _history[0];
                _history.RemoveAt(0);
                _applied.Remove(removed.OpId);
            }
        }

        void ApplyScribeFlags()
        {
            foreach (var participant in _participants.Values)
            {
                participant.isScribe = participant.clientId == _scribeClientId;
                participant.isHost = participant.clientId == _hostClientId;
            }
        }

        static ParticipantInfo Clone(ParticipantInfo source)
        {
            return new ParticipantInfo
            {
                clientId = source.clientId,
                displayName = source.displayName,
                color = source.color,
                isScribe = source.isScribe,
                isHost = source.isHost,
                selectedNodeIds = source.selectedNodeIds == null
                    ? Array.Empty<string>()
                    : (string[])source.selectedNodeIds.Clone(),
            };
        }

        static OpRecord Clone(OpRecord source)
        {
            return new OpRecord
            {
                OpId = source.OpId,
                ClientId = source.ClientId,
                Kind = source.Kind,
                NodeId = source.NodeId,
                PropertyPath = source.PropertyPath,
                OldValue = source.OldValue,
                NewValue = source.NewValue,
                Revision = source.Revision,
                TimestampUnixMs = source.TimestampUnixMs,
            };
        }
    }
}
