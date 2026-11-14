using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    public enum RecoveryPhase { Off, Disconnected, Requesting, Scene, Terrain, Verifying, Ready, Failed }

    public sealed partial class SceneCollabSync
    {
        readonly SceneEditingGate _editingGate = new SceneEditingGate();
        readonly Dictionary<string, PendingProperty> _pendingProperties = new Dictionary<string, PendingProperty>();
        readonly Dictionary<string, SceneOperation> _retiredOperations = new Dictionary<string, SceneOperation>();
        ICollabControlSession _control;
        RecoveryPhase _recoveryPhase;
        string _syncId;
        bool _sceneComplete, _terrainTransferStarted, _syncEndSent, _readySent;
        bool _seenConnected, _canSupplyRecovery;
        double _lastRecoveryProgress, _sentAt, _lastRequestAt = -10;
        string _awaitedOpId, _lastSnapshotKind;
        SceneOperation? _currentOperation;
        int _staleRetries, _sceneFramesTotal, _lastTerrainProgress;

        sealed class PendingProperty
        {
            public string NodeId, Path, Confirmed, Local;
        }

        public bool CanEdit { get { return Enabled && _session != null
            && _session.State == CollabConnectionState.Connected
            && (_control == null || _recoveryPhase == RecoveryPhase.Ready); } }
        public RecoveryPhase RecoveryState { get { return _recoveryPhase; } }
        public string RecoveryStatus { get; private set; }
        public string LastFailure { get; private set; }
        public bool WaitingForAck { get { return _awaitingAck; } }
        public int RecoverySceneCompleted { get { return _session != null && _session.IsHost
            ? Math.Max(0, _sceneFramesTotal - _snapshotOutbox.Count - (_awaitingAck ? 1 : 0)) : _snapshot.Chunks; } }
        public int RecoverySceneTotal { get { return _session != null && _session.IsHost ? _sceneFramesTotal : _snapshot.TotalChunks; } }
        public int RecoveryTerrainTiles { get { return _session != null && _session.IsHost ? _terrain.FullSyncSent : _terrain.RecoveryApplied; } }
        public int RecoveryTerrainTotal { get { return _session != null && _session.IsHost ? _terrain.FullSyncTotal : 0; } }

        public void ClearFailure() { LastFailure = null; }

        void PauseEditing(string reason)
        {
            if (_control == null) return;
            _positions.Finish();
            RecoveryStatus = reason;
            _terrain.EditingAllowed = false;
            _editingGate.SetBlocked(Enabled);
            SceneSaveGuard.RecoveryBlocked = Enabled;
        }

        public void RetryRecovery()
        {
            if (_control == null || !Enabled) return;
            PauseEditing("재동기화 요청 중");
            if (_session.State != CollabConnectionState.Connected)
            {
                _recoveryPhase = RecoveryPhase.Disconnected;
                RecoveryStatus = "연결 대기: 재연결 후 동기화합니다";
                return;
            }
            var now = EditorApplication.timeSinceStartup;
            _recoveryPhase = RecoveryPhase.Requesting;
            if (now - _lastRequestAt < 1) return;
            _lastRequestAt = _lastRecoveryProgress = now;
            if (!_control.SendControl(new WireMessage { type = MessageTypes.SyncRequest }))
                FailRecovery("재동기화 요청을 전송하지 못했습니다. 연결 상태를 확인하세요.");
        }

        void ResetPending()
        {
            if (_control != null && _awaitingAck && _currentOperation.HasValue && !string.IsNullOrEmpty(_awaitedOpId))
                _retiredOperations[_awaitedOpId] = _currentOperation.Value;
            _awaitingAck = _snapshotInFlight = _terrainAfterSnapshot = false;
            _awaitedOpId = null;
            _currentOperation = null;
            _staleRetries = 0;
            _snapshotOutbox.Clear();
            _outbox.Clear();
        }

        void OnControlReceived(WireMessage message)
        {
            if (!Enabled || message == null) return;
            if (message.type == MessageTypes.Error)
            {
                if (!string.IsNullOrEmpty(message.syncId) && message.syncId != _syncId) return;
                if (message.code == ErrorCodes.Locked || message.code == ErrorCodes.StaleRevision) return;
                // A selection-lock request sent just before SyncBegin may be rejected after it.
                // We are already paused in the new epoch; that rejection is not a recovery failure.
                if (message.code == ErrorCodes.SyncInProgress && _recoveryPhase != RecoveryPhase.Ready) return;
                FailRecovery(message.code + ": " + message.message);
                return;
            }
            if (message.type == MessageTypes.SyncBegin)
            {
                if (string.IsNullOrEmpty(message.syncId)) return;
                // Frames already sent were ordered before this barrier. Never keep an old ACK wait.
                RollbackPendingProperties();
                ResetPending();
                // The server's per-room order places every verdict on prior edits before SyncBegin.
                _retiredOperations.Clear();
                _snapshot.Abort();
                _syncId = message.syncId;
                _sceneComplete = _terrainTransferStarted = _syncEndSent = _readySent = false;
                _sceneFramesTotal = 0;
                _lastTerrainProgress = 0;
                _lastRecoveryProgress = EditorApplication.timeSinceStartup;
                _recoveryPhase = RecoveryPhase.Scene;
                PauseEditing("장면 동기화 중 — 편집 일시 중지");
                _terrain.BeginRecovery(_syncId);
                _dirty.Clear();
                _feed.Drain(_dirty);
                _undo.Clear();
                if (!_session.IsHost) _canSupplyRecovery = false;
                if (_session.IsHost)
                {
                    if (!_canSupplyRecovery)
                    {
                        FailRecovery("Host가 복구 도중 종료되어 이 PC의 복사본이 완전한지 확인할 수 없습니다. "
                            + "편집을 중지했습니다. 완전한 씬·터레인을 가진 PC에서 방을 다시 시작하세요.");
                        return;
                    }
                    PublishManifest();
                    if (!QueueSnapshot("room recovery")) FailRecovery(LastNote);
                }
                return;
            }

            if (message.type != MessageTypes.SyncEnd && message.type != MessageTypes.SyncReady) return;
            if (string.IsNullOrEmpty(_syncId) || message.syncId != _syncId) return;
            _lastRecoveryProgress = EditorApplication.timeSinceStartup;
            if (message.type == MessageTypes.SyncEnd)
            {
                var actual = _session.IsHost ? _terrain.FullSyncSent : _terrain.RecoveryApplied;
                var complete = _sceneComplete && _terrain.SkippedDuringRecovery == 0
                    && actual == message.syncTerrainTiles && _recoveryPhase != RecoveryPhase.Failed
                    && MissingAssets == 0 && _terrain.Unsupported == 0;
                if (!complete)
                {
                    FailRecovery(MissingAssets > 0 || _terrain.Unsupported > 0
                        ? "필수 에셋이 없거나 TerrainData가 저장되지 않았습니다. 에셋을 맞춘 뒤 복구하세요."
                        : "동기화 검증 실패: 장면 또는 터레인 타일이 누락됐습니다 ("
                            + actual + "/" + message.syncTerrainTiles + ")");
                }
                else
                {
                    _canSupplyRecovery = true;
                    _recoveryPhase = RecoveryPhase.Verifying;
                    RecoveryStatus = "수신 완료 — 다른 참여자의 완료 확인 대기";
                }
                _readySent = complete;
                _control.SendControl(new WireMessage { type = MessageTypes.SyncReady, syncId = _syncId,
                    accepted = complete, message = complete ? null : LastFailure });
                return;
            }

            if (!message.accepted)
            {
                FailRecovery(message.code + ": " + message.message);
                return;
            }
            if (!_readySent || !_sceneComplete || _recoveryPhase == RecoveryPhase.Failed) return;
            _terrain.EndRecovery();
            _terrain.EditingAllowed = true;
            if (!_terrain.EditingAllowed)
            {
                FailRecovery("터레인의 미확인 변경이 남아 편집을 재개하지 못했습니다. 다시 동기화하세요.");
                return;
            }
            _editingGate.SetBlocked(false);
            PrimeActiveScene();
            _recoveryPhase = RecoveryPhase.Ready;
            RecoveryStatus = "동기화 완료 — 편집 가능";
            _readiness.Reset();
            UpdateSaveGuard();
        }

        void TickRecovery(double now)
        {
            if (RecoveryTerrainTiles != _lastTerrainProgress)
            {
                _lastTerrainProgress = RecoveryTerrainTiles;
                _lastRecoveryProgress = now;
            }
            if (_awaitingAck && now - _sentAt > 12)
            {
                RollbackPendingProperties();
                ResetPending();
                FailRecovery("서버 확인 응답 시간 초과. 재동기화가 필요합니다.");
                RetryRecovery();
            }
            if (_session == null || _session.State != CollabConnectionState.Connected) return;
            if (_recoveryPhase == RecoveryPhase.Requesting && now - _lastRequestAt > 5)
                RetryRecovery();
            if (_session.IsHost && _sceneComplete && _terrainTransferStarted
                && !_terrain.FullSyncInProgress && !_syncEndSent && _recoveryPhase != RecoveryPhase.Failed)
            {
                _syncEndSent = _control.SendControl(new WireMessage { type = MessageTypes.SyncEnd,
                    syncId = _syncId, syncTerrainTiles = _terrain.FullSyncSent });
                _recoveryPhase = RecoveryPhase.Verifying;
                RecoveryStatus = "터레인 전송 완료 — 참여자 확인 대기";
                _lastRecoveryProgress = now;
            }
            else if (_sceneComplete && _terrain.FullSyncInProgress)
            {
                _recoveryPhase = RecoveryPhase.Terrain;
                RecoveryStatus = "터레인 동기화 중 (" + RecoveryTerrainTiles + " 타일)";
            }
            if (_recoveryPhase != RecoveryPhase.Ready && _recoveryPhase != RecoveryPhase.Failed
                && now - _lastRecoveryProgress > 60)
                FailRecovery("동기화 진행이 멈췄습니다. 실패 사유를 확인하고 다시 동기화하세요.");
        }

        void FailRecovery(string reason)
        {
            if (_recoveryPhase != RecoveryPhase.Failed || string.IsNullOrEmpty(LastFailure))
                LastFailure = string.IsNullOrEmpty(reason) ? "동기화 실패" : reason;
            if (_control == null) return;
            var firstFailure = _recoveryPhase != RecoveryPhase.Failed;
            _recoveryPhase = RecoveryPhase.Failed;
            RollbackPendingProperties();
            ResetPending();
            _snapshot.Abort();
            PauseEditing(LastFailure);
            _terrain.CancelRecovery();
            if (firstFailure && !string.IsNullOrEmpty(_syncId) && _session.State == CollabConnectionState.Connected)
                _control.SendControl(new WireMessage { type = MessageTypes.SyncReady, syncId = _syncId,
                    accepted = false, message = LastFailure });
        }

        void OnTerrainFailure(string reason) { FailRecovery(reason); }
        void OnSaveBlocked(string reason) { LastFailure = reason; }
        void OnSessionError(string code, string detail)
        {
            // Protocol rejections are handled with their opId/syncId, so a late rejection cannot
            // stop a newer recovery. Only transport/parser errors arrive here without a wire frame.
            if (_control != null)
            {
                if (Enabled && (code == ErrorCodes.NotInitialized
                    || (code == ErrorCodes.Malformed && detail != null
                        && (detail.StartsWith("send failed:", StringComparison.Ordinal)
                            || detail.StartsWith("unreadable message:", StringComparison.Ordinal)))))
                    FailRecovery(code + ": " + detail);
                return;
            }
            LastFailure = code + ": " + detail;
        }

        void EnqueueLocal(SceneOperation operation)
        {
            if (operation.Kind == OpKinds.SetProperty || operation.Kind == OpKinds.SetPrefabOverride)
            {
                var key = operation.NodeId + "|" + operation.PropertyPath;
                PendingProperty pending;
                if (!_pendingProperties.TryGetValue(key, out pending))
                {
                    pending = new PendingProperty { NodeId = operation.NodeId, Path = operation.PropertyPath,
                        Confirmed = operation.OldValue };
                    _pendingProperties.Add(key, pending);
                }
                pending.Local = operation.NewValue;
            }
            _outbox.Enqueue(operation);
        }

        void ConfirmProperty(SceneOperation operation)
        {
            var key = operation.NodeId + "|" + operation.PropertyPath;
            PendingProperty pending;
            if (!_pendingProperties.TryGetValue(key, out pending)) return;
            // A peer's value may have landed while our original operation was retried against a
            // newer revision. The server does not echo our accepted value to us, so restore the
            // latest queued local intent here, including any later coalesced edit.
            if (pending.Path == ScenePropertyAccess.LocalPosition) _positions.Cancel(operation.NodeId);
            GameObject go;
            string error, current;
            if (_registry.TryResolve(pending.NodeId, out go) && go != null
                && ScenePropertyAccess.TryRead(go, pending.Path, _registry, out current))
            {
                if (current == pending.Confirmed && current != pending.Local
                    && ScenePropertyAccess.TryWrite(go, pending.Path, _registry, pending.Local, out error))
                    _diff.NoteRemoteValue(pending.NodeId, pending.Path, pending.Local);
                else if (current == pending.Local)
                    _diff.NoteRemoteValue(pending.NodeId, pending.Path, pending.Local);
                // Otherwise a newer local UI edit has not reached the diff yet; leave it intact.
            }
            _undo.NoteWrite(pending.NodeId, pending.Path, _session.ClientId);
            pending.Confirmed = operation.NewValue;
            if (pending.Local == pending.Confirmed) _pendingProperties.Remove(key);
        }

        bool ResolveRetiredOperation(WireMessage verdict)
        {
            SceneOperation operation;
            if (!Enabled || verdict == null || string.IsNullOrEmpty(verdict.opId)
                || !_retiredOperations.TryGetValue(verdict.opId, out operation)) return false;
            _retiredOperations.Remove(verdict.opId);
            if (!verdict.accepted && !verdict.duplicate) return true;
            if (operation.PropertyPath == ScenePropertyAccess.LocalPosition) _positions.Cancel(operation.NodeId);
            var wire = operation.ToWire(_session.ClientId, _session.RoomName, verdict.revision, verdict.opId);
            string note;
            if (!_applier.Apply(wire, out note))
            {
                _canSupplyRecovery = false;
                FailRecovery("지연된 승인 결과를 적용하지 못했습니다: " + note);
                return true;
            }
            _undo.NoteWrite(operation.NodeId, operation.PropertyPath, _session.ClientId);
            return true;
        }

        void RollbackPendingProperties()
        {
            _positions.Finish();
            foreach (var pending in _pendingProperties.Values)
            {
                GameObject go;
                if (!_registry.TryResolve(pending.NodeId, out go) || go == null) continue;
                string current;
                if (!ScenePropertyAccess.TryRead(go, pending.Path, _registry, out current) || current != pending.Local) continue;
                string error;
                if (ScenePropertyAccess.TryWrite(go, pending.Path, _registry, pending.Confirmed, out error))
                    _diff.NoteRemoteValue(pending.NodeId, pending.Path, pending.Confirmed);
            }
            _pendingProperties.Clear();
        }
    }
}
