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
        readonly int _historyLimit;

        long _revision;
        string _scribeClientId;

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

                if (string.IsNullOrEmpty(_scribeClientId) || !_participants.ContainsKey(_scribeClientId))
                {
                    _scribeClientId = clientId;
                }

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

                if (_scribeClientId == clientId)
                {
                    _scribeClientId = _participants.Keys.OrderBy(k => k, StringComparer.Ordinal).FirstOrDefault();
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
