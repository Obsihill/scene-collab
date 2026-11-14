using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Native Terrain sculpt/paint collaboration. The baseline is authoritative, a pending lock
    /// holds a reverted stroke, and a granted stroke remains provisional until its tile ACKs.
    /// Trees, details, holes, terrain layout and layer changes are outside this sample protocol.
    /// </summary>
    internal sealed class TerrainSync
    {
        public const double PollSeconds = 0.6;
        public const double SplatPollSeconds = 2.0; // Kept for legacy tests/diagnostics.
        internal const double IdleLeaseSeconds = 2.0;
        internal const double AckTimeoutSeconds = 15.0;
        const int TilesPerTick = 8;
        public const string SupportedFeatures = "Terrain collaboration: sculpt and texture paint only; trees, details, holes and layout changes are unsupported.";

        sealed class LocalEdit
        {
            public TerrainBaseline Baseline;
            public string Kind, LockOpId, RequestedLeaseId, LeaseId;
            public RectInt Region, Sectors;
            public List<TerrainCapturedPatch> Tiles;
            public int Accepted;
            public bool Awaiting, Invalidated;
            public double Started, SentAt;
            public long RecoveryGeneration;
        }

        sealed class CanceledPatch
        {
            public TerrainBaseline Baseline;
            public TerrainCapturedPatch Patch;
            public long RecoveryGeneration;
            public double SentAt;
        }

        sealed class OwnLease
        {
            public TerrainRegionLease Lease;
            public double LastActivity;
            public bool Listed = true;
        }

        readonly Dictionary<string, TerrainBaseline> _baselines = new Dictionary<string, TerrainBaseline>(StringComparer.Ordinal);
        readonly Dictionary<TerrainData, TerrainBaseline> _byData = new Dictionary<TerrainData, TerrainBaseline>();
        readonly List<TerrainBaseline> _active = new List<TerrainBaseline>();
        readonly List<GameObject> _roots = new List<GameObject>();
        readonly List<Terrain> _components = new List<Terrain>();
        readonly List<Terrain> _componentBuffer = new List<Terrain>();
        readonly Dictionary<string, LocalEdit> _edits = new Dictionary<string, LocalEdit>(StringComparer.Ordinal);
        readonly Dictionary<string, OwnLease> _owned = new Dictionary<string, OwnLease>(StringComparer.Ordinal);
        readonly List<TerrainRegionLease> _locks = new List<TerrainRegionLease>();
        readonly Dictionary<string, string> _unlockRequests = new Dictionary<string, string>(StringComparer.Ordinal);
        readonly HashSet<string> _releasing = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, double> _snapshotAcks = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly HashSet<string> _recoveryTiles = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<string, CanceledPatch> _canceledPatches = new Dictionary<string, CanceledPatch>(StringComparer.Ordinal);
        readonly List<LocalEdit> _editBuffer = new List<LocalEdit>();
        readonly List<string> _idBuffer = new List<string>();
        readonly Func<double> _clock;
        readonly Func<long> _utc;
        readonly TerrainRegionOverlay _overlay;

        ISceneCollabSession _session;
        ICollabControlSession _control;
        TerrainBaseline _testBaseline;
        static int s_Applying;
        bool _editingAllowed, _recovering, _failureRaised, _recoveryRequired, _canceling;
        bool _fullSyncRequested;
        string _recoveryId, _fullSyncId;
        IEnumerator<WireMessage> _fullSync;
        double _nextPoll;
        long _serverOffset;
        long _recoveryGeneration;
        int _tilesRead;

        internal TerrainSync(Func<double> clock = null, Func<long> utc = null)
        {
            _clock = clock ?? (() => EditorApplication.timeSinceStartup);
            _utc = utc ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            _overlay = new TerrainRegionOverlay(this);
        }

        public int Sent { get; private set; }
        public int Applied { get; private set; }
        public int Skipped { get; private set; }
        public int Unsupported { get; private set; }
        public int TilesRead => _tilesRead;
        public int KnownTerrains => _active.Count;
        public string LastNote { get; private set; } = SupportedFeatures;
        public int FullSyncSent { get; private set; }
        public int FullSyncTotal { get; private set; }
        public int RecoveryApplied { get; private set; }
        public int SkippedDuringRecovery { get; private set; }
        public event Action<string> Failed;

        // A queued request counts BEFORE Tick. The parent must not close the recovery while
        // either generation or reliable acknowledgements are still outstanding.
        public bool FullSyncInProgress => _fullSyncRequested || _fullSync != null || _snapshotAcks.Count != 0;

        public bool EditingAllowed
        {
            get => _editingAllowed && !_recovering && !_recoveryRequired && Connected;
            set
            {
                _editingAllowed = value;
                if (!value) CancelEditing();
            }
        }

        bool Connected => _session != null && _session.State == CollabConnectionState.Connected;
        internal long ServerNow => _utc() + _serverOffset;
        internal IReadOnlyList<Terrain> VisibleTerrains => _components;
        internal IReadOnlyList<TerrainRegionLease> RegionLocks => _locks;
        internal string ClientId => _session == null ? null : _session.ClientId;
        internal string GuidFor(TerrainData data) => data != null && _byData.TryGetValue(data, out var b) ? b.Guid : null;

        public void Bind(ISceneCollabSession session)
        {
            if (ReferenceEquals(_session, session)) return;
            Unbind();
            _session = session;
            _control = session as ICollabControlSession;
            if (_session == null) return;
            _editingAllowed = _control == null;
            RefreshTerrainMap(); // Capture BEFORE registering post-write callbacks.
            _session.TerrainReceived += OnTerrainReceived;
            _session.StateChanged += OnStateChanged;
            if (_control != null) _control.ControlReceived += OnControlReceived;
            TerrainCallbacks.heightmapChanged += OnHeightmapChanged;
            TerrainCallbacks.textureChanged += OnTextureChanged;
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            Undo.undoRedoPerformed += OnUndoRedo;
            _overlay.Bind();
        }

        public void Unbind()
        {
            _editingAllowed = false;
            CancelEditing();
            TerrainCallbacks.heightmapChanged -= OnHeightmapChanged;
            TerrainCallbacks.textureChanged -= OnTextureChanged;
            EditorApplication.hierarchyChanged -= OnHierarchyChanged;
            Undo.undoRedoPerformed -= OnUndoRedo;
            _overlay.Unbind();
            if (_session != null)
            {
                _session.TerrainReceived -= OnTerrainReceived;
                _session.StateChanged -= OnStateChanged;
            }
            if (_control != null) _control.ControlReceived -= OnControlReceived;
            _session = null;
            _control = null;
            Reset();
        }

        public void Reset()
        {
            _editingAllowed = false;
            CancelEditing();
            StopFullSync();
            _baselines.Clear(); _byData.Clear(); _active.Clear(); _components.Clear();
            _locks.Clear(); _owned.Clear(); _unlockRequests.Clear(); _releasing.Clear();
            _canceledPatches.Clear(); _recoveryTiles.Clear();
            _testBaseline = null;
            _nextPoll = 0; _serverOffset = 0; _recoveryGeneration = 0;
            _recovering = _recoveryRequired = _failureRaised = false;
            _recoveryId = _fullSyncId = null;
            FullSyncSent = FullSyncTotal = RecoveryApplied = SkippedDuringRecovery = 0;
            LastNote = SupportedFeatures;
            _editingAllowed = _session != null && _control == null;
            if (_session != null) RefreshTerrainMap();
        }

        public void BeginRecovery(string syncId)
        {
            if (_recovering && _recoveryId == syncId) return;
            _recoveryGeneration++;
            _recovering = true;
            _editingAllowed = false;
            _recoveryId = syncId;
            CancelEditing();
            StopFullSync();
            _failureRaised = _recoveryRequired = false;
            _recoveryTiles.Clear();
            FullSyncSent = FullSyncTotal = RecoveryApplied = SkippedDuringRecovery = 0;
            LastNote = "Terrain recovery in progress; sculpt and paint are paused.";
            RefreshTerrainMap();
        }

        public void EndRecovery()
        {
            _recovering = false;
            _recoveryId = null;
            _recoveryRequired = SkippedDuringRecovery != 0 || _canceledPatches.Count != 0;
            // Only the parent, after SyncReady, enables EditingAllowed.
            LastNote = _recoveryRequired ? "Terrain recovery failed; editing remains paused." : SupportedFeatures;
        }

        /// <summary>Abort transmission as well as editing; setting EditingAllowed=false alone does not stop a snapshot.</summary>
        public void CancelRecovery()
        {
            _editingAllowed = false;
            CancelEditing();
            StopFullSync();
            _recovering = false;
            _recoveryId = null;
            _recoveryRequired = true;
            LastNote = "Terrain recovery canceled; editing remains paused.";
        }

        public void RequestFullSync() => RequestFullSync(_recoveryId ?? System.Guid.NewGuid().ToString("N"));

        public void RequestFullSync(string syncId)
        {
            if (_session == null || !_session.IsHost) return;
            if (_control != null && (string.IsNullOrEmpty(syncId)))
            { Fail("Terrain recovery requires a syncId."); return; }
            if (_control != null && (!_recovering || _recoveryId != syncId)) BeginRecovery(syncId);
            StopFullSync();
            FullSyncSent = 0;
            RefreshTerrainMap();
            FullSyncTotal = 0;
            foreach (var b in _active)
            {
                var side = (b.HeightResolution + TerrainPatchCodec.HeightTile - 1) / TerrainPatchCodec.HeightTile;
                FullSyncTotal += side * side;
                if (b.Splat == null) continue;
                var tile = TerrainPatchCodec.TileForLayers(b.Layers);
                side = (b.AlphaResolution + tile - 1) / tile;
                FullSyncTotal += side * side;
            }
            _fullSyncId = syncId;
            _fullSyncRequested = true;
        }

        public void Tick()
        {
            if (_session == null) return;
            if (!Connected) { OnStateChanged(); return; }
            var now = _clock();
            if (now >= _nextPoll)
            {
                _nextPoll = now + PollSeconds;
                RefreshTerrainMap();
                foreach (var b in _active)
                {
                    if (!b.MatchesLayout) { Fail("Terrain layout changed; restore matching assets before recovery."); return; }
                    if (VersionsChanged(b)) b.AuditRequested = true;
                }
            }

            PumpFullSync(now);
            _editBuffer.Clear(); _editBuffer.AddRange(_edits.Values);
            foreach (var edit in _editBuffer)
            {
                if (!_edits.ContainsKey(edit.Baseline.Guid)) continue;
                if (!string.IsNullOrEmpty(edit.LockOpId))
                {
                    if (now - edit.Started > AckTimeoutSeconds)
                    {
                        _edits.Remove(edit.Baseline.Guid);
                        Release(edit.Baseline.Guid, edit.RequestedLeaseId);
                        LastNote = "Terrain lock timed out; the stroke was restored. Retry.";
                    }
                }
                else if (edit.Awaiting)
                {
                    // Lease expiry is NOT an ACK. Keep the rollback baseline and operation id
                    // alive until the verdict arrives or recovery resolves an uncertain send.
                    if (now - edit.SentAt > AckTimeoutSeconds)
                    { Fail("Terrain acknowledgement timed out; recovery is required."); break; }
                }
                else SendNext(edit);
            }
            ReleaseIdleLeases(now);
            AuditUnreportedChanges();
        }

        void OnStateChanged()
        {
            if (Connected) return;
            _editingAllowed = false;
            CancelEditing();
            StopFullSync();
            _locks.Clear(); _owned.Clear(); _releasing.Clear(); _unlockRequests.Clear();
        }

        void OnHierarchyChanged()
        {
            if (_session != null && s_Applying == 0) RefreshTerrainMap();
        }

        void OnUndoRedo()
        {
            // Undo and direct texture writes do not consistently raise TerrainCallbacks.
            foreach (var b in _active) b.AuditRequested = true;
        }

        void OnHeightmapChanged(Terrain terrain, RectInt region, bool synched)
            => OnChanged(terrain, TerrainPatchCodec.KindHeight, region, synched);

        void OnTextureChanged(Terrain terrain, string textureName, RectInt region, bool synched)
        {
            if (s_Applying != 0 || _session == null) return;
            if (textureName != TerrainData.AlphamapTextureName)
            {
                if (terrain != null && _byData.ContainsKey(terrain.terrainData)) LastNote = SupportedFeatures;
                return;
            }
            OnChanged(terrain, TerrainPatchCodec.KindSplat, region, synched);
        }

        void OnChanged(Terrain terrain, string kind, RectInt region, bool synched)
        {
            if (s_Applying != 0 || _session == null || terrain == null || terrain.terrainData == null) return;
            if (!_byData.TryGetValue(terrain.terrainData, out var b))
            {
                if (terrain.gameObject.scene == SceneManager.GetActiveScene())
                    Fail("Terrain changed before a baseline was available; save its asset and recover before editing.");
                return;
            }
            try
            {
                if (!synched)
                {
                    s_Applying++;
                    try
                    {
                        if (kind == TerrainPatchCodec.KindHeight) b.Data.SyncHeightmap();
                        else b.Data.SyncTexture(TerrainData.AlphamapTextureName);
                    }
                    finally { s_Applying--; }
                }
                HandleChanged(b, kind, region);
                ObserveVersions(b);
            }
            catch (Exception ex) { Fail("Terrain callback failed: " + ex.Message); }
        }

        void HandleChanged(TerrainBaseline b, string kind, RectInt region)
        {
            if (!b.MatchesLayout) { Fail("Terrain layout changes are unsupported; recover matching terrain assets."); return; }
            var resolution = b.Resolution(kind);
            var x = Mathf.Clamp(region.x, 0, resolution);
            var z = Mathf.Clamp(region.y, 0, resolution);
            region = new RectInt(x, z, Mathf.Clamp(region.xMax, x, resolution) - x,
                Mathf.Clamp(region.yMax, z, resolution) - z);
            if (region.width == 0 || region.height == 0 || (kind == TerrainPatchCodec.KindSplat && b.Splat == null)) return;

            if (_edits.TryGetValue(b.Guid, out var pending))
            {
                RestoreVisible(b, kind, region, pending);
                LastNote = "Waiting for terrain lock/acknowledgement; additional edits were restored.";
                return;
            }

            var tiles = b.Capture(kind, region, ref _tilesRead);
            if (tiles.Count == 0) return;
            if (!EditingAllowed || (_control == null && !_session.IsHost))
            {
                RestoreVisible(b, kind, region, null);
                LastNote = "Terrain editing is paused; the local change was restored.";
                return;
            }
            if (_control == null)
            {
                foreach (var tile in tiles)
                {
                    if (!_session.SendTerrain(tile.Message))
                    { RestoreVisible(b, kind, region, null); Fail("Terrain send failed."); return; }
                    Apply(b, tile, true);
                    Sent++;
                }
                return;
            }

            var sectors = Map(resolution, region);
            foreach (var lease in _locks)
                if (lease.terrainGuid == b.Guid && lease.clientId != ClientId && lease.expiresAt > ServerNow
                    && TerrainRegionRules.Overlaps(sectors.x, sectors.y, sectors.width, sectors.height, lease))
                {
                    RestoreVisible(b, kind, region, null);
                    LastNote = "Terrain region belongs to " + (lease.displayName ?? lease.clientId) + "; the stroke was restored.";
                    return;
                }

            var edit = new LocalEdit { Baseline = b, Kind = kind, Region = region, Sectors = sectors,
                Tiles = tiles, Started = _clock(), RecoveryGeneration = _recoveryGeneration };
            _edits.Add(b.Guid, edit);
            var own = FindOwnLease(b.Guid, sectors);
            if (own != null)
            {
                edit.LeaseId = own.leaseId;
                ShowEdit(edit);
                SendNext(edit);
                return;
            }

            // Capture first, restore immediately, and only then issue the lock request. Even a
            // synchronous fake grant cannot make the captured stroke an unleased local write.
            RestoreVisible(b, kind, region, null);
            edit.LockOpId = NewId();
            // The server allocates a new lease id; a supplied id means renew/expand.
            // opId, not the eventual lease id, correlates this acquisition request.
            edit.RequestedLeaseId = null;
            LastNote = "Requesting terrain sectors; the first stroke is buffered.";
            if (!_control.SendControl(new WireMessage { type = MessageTypes.TerrainLock, opId = edit.LockOpId,
                terrainGuid = b.Guid, terrainLeaseId = edit.RequestedLeaseId,
                regionX = sectors.x, regionZ = sectors.y, regionWidth = sectors.width, regionHeight = sectors.height }))
                Fail("Terrain lock request could not be sent.");
        }

        void ShowEdit(LocalEdit edit)
        {
            foreach (var patch in edit.Tiles) Apply(edit.Baseline, patch, false);
        }

        void SendNext(LocalEdit edit)
        {
            if (edit.Awaiting || !EditingAllowed || edit.Accepted >= edit.Tiles.Count) return;
            var lease = FindOwnLease(edit.Baseline.Guid, edit.Sectors, edit.LeaseId);
            if (lease == null) { Fail("Terrain lease expired before the stroke was accepted; recovery is required."); return; }
            var message = edit.Tiles[edit.Accepted].Message;
            message.terrainLeaseId = edit.LeaseId;
            edit.Awaiting = true;
            edit.SentAt = _clock();
            _owned[edit.LeaseId].LastActivity = edit.SentAt;
            if (_session.SendTerrain(message)) Sent++;
            else Fail("Terrain patch could not be sent; recovery is required.");
        }

        void OnControlReceived(WireMessage message)
        {
            if (message == null) return;
            if (message.serverTime > 0) _serverOffset = message.serverTime - _utc();
            if (message.type == MessageTypes.TerrainLocks) OnLocks(message);
            else if (message.type == MessageTypes.TerrainAck) OnAcknowledged(message);
            else if (message.type == MessageTypes.SyncReady && !message.accepted && message.syncId == _recoveryId)
                CancelRecovery();
            // SyncBegin belongs to the parent; handling it here would reset the same recovery twice.
        }

        void OnLocks(WireMessage message)
        {
            _locks.Clear();
            if (message.terrainLocks != null)
                foreach (var lease in message.terrainLocks)
                    if (lease != null && !string.IsNullOrEmpty(lease.leaseId) && !string.IsNullOrEmpty(lease.terrainGuid)
                        && TerrainRegionRules.IsValid(lease.x, lease.z, lease.width, lease.height)) _locks.Add(lease);

            if (!string.IsNullOrEmpty(message.opId) && _unlockRequests.TryGetValue(message.opId, out var released))
            {
                _unlockRequests.Remove(message.opId);
                _owned.Remove(released);
                // Keep a tombstone while a stale/full-list echo still contains this lease.
            }
            foreach (var own in _owned.Values) own.Listed = false;
            foreach (var lease in _locks)
                if (_owned.TryGetValue(lease.leaseId, out var own)) { own.Lease = lease; own.Listed = true; }

            LocalEdit requested = null;
            foreach (var edit in _edits.Values)
                if (!string.IsNullOrEmpty(edit.LockOpId) && edit.LockOpId == message.opId) { requested = edit; break; }
            if (requested != null)
            {
                requested.RequestedLeaseId = message.terrainLeaseId;
                var grant = FindGrant(requested);
                requested.LockOpId = null;
                if (!message.accepted || grant == null || !EditingAllowed || requested.Invalidated)
                {
                    _edits.Remove(requested.Baseline.Guid);
                    if (grant != null) Release(grant.terrainGuid, grant.leaseId);
                    LastNote = requested.Invalidated
                        ? "A remote patch intersected the buffered stroke. It was discarded; retry on the updated terrain."
                        : "Terrain lock denied or expired (" + (message.code ?? "unavailable") + "); the stroke was restored.";
                }
                else
                {
                    requested.LeaseId = grant.leaseId;
                    _owned[grant.leaseId] = new OwnLease { Lease = grant, LastActivity = _clock() };
                    ShowEdit(requested);
                    SendNext(requested);
                }
            }

            // A grant can arrive after pause, timeout or Reset. It must be released, never
            // resurrect a discarded stroke or quietly become authorization for another one.
            foreach (var lease in _locks.ToArray())
                if (lease.clientId == ClientId && !_owned.ContainsKey(lease.leaseId) && !IsRequestedLease(lease))
                    Release(lease.terrainGuid, lease.leaseId);
            SceneView.RepaintAll();
        }

        bool IsRequestedLease(TerrainRegionLease lease)
        {
            foreach (var edit in _edits.Values)
                if (!string.IsNullOrEmpty(edit.LockOpId) && lease.leaseId == edit.RequestedLeaseId) return true;
            return false;
        }

        TerrainRegionLease FindGrant(LocalEdit edit)
        {
            TerrainRegionLease covering = null;
            foreach (var lease in _locks)
                if (lease.terrainGuid == edit.Baseline.Guid && lease.clientId == ClientId && lease.expiresAt > ServerNow
                    && !_releasing.Contains(lease.leaseId)
                    && (string.IsNullOrEmpty(edit.RequestedLeaseId) || edit.RequestedLeaseId == lease.leaseId)
                    && TerrainRegionRules.Covers(lease, edit.Sectors.x, edit.Sectors.y, edit.Sectors.width, edit.Sectors.height))
                {
                    if (lease.leaseId == edit.RequestedLeaseId) return lease;
                    covering = lease; // A server may return a pre-existing covering lease.
                }
            return covering;
        }

        void OnAcknowledged(WireMessage message)
        {
            if (string.IsNullOrEmpty(message.opId)) return;
            if (_snapshotAcks.Remove(message.opId))
            {
                if (!message.accepted) { SkippedDuringRecovery++; Fail("Terrain snapshot rejected: " + message.code); }
                return;
            }
            if (_canceledPatches.TryGetValue(message.opId, out var canceled))
            {
                _canceledPatches.Remove(message.opId);
                if (!message.accepted) return; // The speculative samples were already restored.

                // A pause is not a server verdict. In particular, RetryRecovery pauses locally
                // before the ordered terrain ACK and SyncBegin can arrive. Preserve acceptance
                // in the authoritative baseline before that host baseline is snapshotted.
                // Once a new barrier has begun, a late acceptance violates that ordering and
                // must not overwrite samples received/published in the new recovery generation.
                if (canceled.RecoveryGeneration != _recoveryGeneration || _recovering
                    || !string.Equals(message.syncId ?? string.Empty, canceled.Patch.Message.syncId ?? string.Empty, StringComparison.Ordinal))
                {
                    SkippedDuringRecovery++;
                    Fail("Accepted terrain acknowledgement crossed a recovery boundary; recovery must restart.");
                    return;
                }
                var b = canceled.Baseline;
                if (!Connected || !b.MatchesLayout || !_active.Contains(b)
                    || !_baselines.TryGetValue(b.Guid, out var current) || !ReferenceEquals(current, b))
                { Fail("Accepted canceled terrain patch no longer has its original baseline; recovery is required."); return; }
                try { Apply(b, canceled.Patch, true); }
                catch (Exception ex) { Fail("Accepted canceled terrain patch could not be applied: " + ex.Message); return; }
                LastNote = "Terrain patch accepted while paused; its samples were retained for recovery.";
                return;
            }
            LocalEdit target = null;
            foreach (var edit in _edits.Values)
                if (edit.Awaiting && edit.Tiles[edit.Accepted].Message.opId == message.opId) { target = edit; break; }
            if (target == null) return;
            if (!message.accepted) { Fail("Terrain patch rejected: " + (message.code ?? "unknown") + ". Recovery is required."); return; }
            try { Apply(target.Baseline, target.Tiles[target.Accepted], true); }
            catch (Exception ex) { Fail("Accepted terrain patch could not be applied: " + ex.Message); return; }
            target.Awaiting = false;
            target.Accepted++;
            if (_owned.TryGetValue(target.LeaseId, out var own))
            {
                own.LastActivity = _clock();
                own.Listed = true;
                // An accepted patch renews the lease (shared protocol rule).
                own.Lease.expiresAt = Math.Max(own.Lease.expiresAt, ServerNow + TerrainRegionRules.LeaseDurationMs);
            }
            if (target.Accepted == target.Tiles.Count) _edits.Remove(target.Baseline.Guid);
            LastNote = "Terrain patch accepted.";
        }

        void OnTerrainReceived(WireMessage message)
        {
            if (message == null) return;
            if (_control != null && message.snapshot && (!_recovering || message.syncId != _recoveryId))
                return; // An old recovery must not overwrite a newer generation.
            if (_control != null && _recovering && !message.snapshot)
            { Skip("Live terrain patch arrived during recovery."); return; }
            if (!TerrainCapturedPatch.TryDecode(message, out var patch)) { Skip("Malformed terrain tile."); return; }
            if (!_baselines.TryGetValue(message.terrainGuid, out var b) || b.Data == null || !_active.Contains(b))
            {
                RefreshTerrainMap();
                if (!_baselines.TryGetValue(message.terrainGuid, out b) || !_active.Contains(b))
                { Skip("Terrain patch references an asset absent from the active scene."); return; }
            }
            if (!b.MatchesLayout || b.Resolution(message.terrainKind) != message.terrainResolution
                || (patch.Splat != null && (b.Splat == null || b.Layers != message.terrainLayers)))
            { Skip("Terrain resolution or layer layout differs."); return; }

            var recoveryTile = message.terrainGuid + ":" + message.terrainKind + ":" + message.terrainX + ":"
                + message.terrainZ + ":" + message.terrainWidth + ":" + message.terrainHeight;
            if (_recovering && message.snapshot && _recoveryTiles.Contains(recoveryTile)) return;
            var conflict = false;
            if (_edits.TryGetValue(b.Guid, out var edit))
            {
                if (edit.Awaiting && edit.Tiles[edit.Accepted].Message.opId == message.opId) return;
                if (edit.Sectors.Overlaps(Map(message.terrainResolution, patch.Area)))
                {
                    if (!string.IsNullOrEmpty(edit.LockOpId)) edit.Invalidated = true;
                    else conflict = true;
                }
            }
            try { Apply(b, patch, true); }
            catch (Exception ex) { Skip("Terrain apply failed: " + ex.Message); return; }
            Applied++;
            if (_recovering && message.snapshot) { _recoveryTiles.Add(recoveryTile); RecoveryApplied++; }
            if (conflict) Fail("A remote patch intersected an unacknowledged local stroke; recovery is required.");
            else LastNote = "Applied terrain " + message.terrainKind + " tile.";
        }

        void Apply(TerrainBaseline b, TerrainCapturedPatch patch, bool commit)
        {
            s_Applying++;
            try
            {
                patch.Write(b.Data);
                if (commit) b.Commit(patch);
                EditorUtility.SetDirty(b.Data);
                ObserveVersions(b);
            }
            finally { s_Applying--; }
        }

        void RestoreVisible(TerrainBaseline b, string kind, RectInt region, LocalEdit edit)
        {
            if (b.Data == null) return;
            s_Applying++;
            try
            {
                b.Restore(kind, region);
                if (edit != null && string.IsNullOrEmpty(edit.LockOpId) && !string.IsNullOrEmpty(edit.LeaseId))
                    for (var i = edit.Accepted; i < edit.Tiles.Count; i++) edit.Tiles[i].Write(b.Data);
                ObserveVersions(b);
            }
            finally { s_Applying--; }
        }

        void CancelEditing()
        {
            if (_canceling) return;
            _canceling = true;
            try
            {
                var edits = new List<LocalEdit>(_edits.Values);
                _edits.Clear();
                foreach (var edit in edits)
                {
                    if (edit.Awaiting)
                    {
                        var patch = edit.Tiles[edit.Accepted];
                        _canceledPatches[patch.Message.opId] = new CanceledPatch { Baseline = edit.Baseline,
                            Patch = patch, RecoveryGeneration = edit.RecoveryGeneration, SentAt = edit.SentAt };
                        _recoveryRequired = true;
                    }
                    try { RestoreVisible(edit.Baseline, edit.Kind, edit.Region, null); }
                    catch (Exception ex) { LastNote = "Terrain rollback failed: " + ex.Message; _recoveryRequired = true; }
                    if (!string.IsNullOrEmpty(edit.RequestedLeaseId)) Release(edit.Baseline.Guid, edit.RequestedLeaseId);
                }
                var owned = new List<OwnLease>(_owned.Values);
                _owned.Clear();
                foreach (var own in owned) Release(own.Lease.terrainGuid, own.Lease.leaseId);
            }
            finally { _canceling = false; }
        }

        TerrainRegionLease FindOwnLease(string guid, RectInt sectors, string requiredId = null)
        {
            foreach (var own in _owned.Values)
            {
                var lease = own.Lease;
                if (own.Listed && lease.terrainGuid == guid && lease.clientId == ClientId && lease.expiresAt > ServerNow
                    && (requiredId == null || lease.leaseId == requiredId) && !_releasing.Contains(lease.leaseId)
                    && TerrainRegionRules.Covers(lease, sectors.x, sectors.y, sectors.width, sectors.height)) return lease;
            }
            return null;
        }

        void ReleaseIdleLeases(double now)
        {
            _idBuffer.Clear();
            foreach (var pair in _owned)
            {
                var busy = false;
                foreach (var edit in _edits.Values) if (edit.LeaseId == pair.Key) { busy = true; break; }
                if (!busy && (now - pair.Value.LastActivity >= IdleLeaseSeconds || pair.Value.Lease.expiresAt <= ServerNow))
                    _idBuffer.Add(pair.Key);
            }
            foreach (var id in _idBuffer)
                if (_owned.TryGetValue(id, out var own)) { _owned.Remove(id); Release(own.Lease.terrainGuid, id); }
        }

        void Release(string guid, string leaseId)
        {
            if (_control == null || string.IsNullOrEmpty(leaseId) || !_releasing.Add(leaseId)) return;
            var opId = NewId();
            _unlockRequests[opId] = leaseId;
            if (!Connected || !_control.SendControl(new WireMessage { type = MessageTypes.TerrainUnlock,
                opId = opId, terrainGuid = guid, terrainLeaseId = leaseId })) _unlockRequests.Remove(opId);
        }

        void PumpFullSync(double now)
        {
            if (_fullSyncRequested && _canceledPatches.Count != 0)
            {
                // A live patch queued behind another peer's SyncRequest can still be awaiting
                // rejection. Never publish an older baseline while any server verdict is unknown.
                foreach (var canceled in _canceledPatches.Values)
                    if (now - canceled.SentAt > AckTimeoutSeconds)
                    { SkippedDuringRecovery++; Fail("Canceled terrain acknowledgement timed out before the recovery snapshot."); return; }
                return;
            }
            foreach (var sentAt in _snapshotAcks.Values)
                if (now - sentAt > AckTimeoutSeconds)
                { SkippedDuringRecovery++; Fail("Terrain snapshot acknowledgement timed out."); return; }
            try
            {
                if (_fullSyncRequested)
                {
                    _fullSyncRequested = false;
                    RefreshTerrainMap();
                    _fullSync = FullSyncTiles(new List<TerrainBaseline>(_active)).GetEnumerator();
                }
                if (_fullSync == null) return;
                // Bound both the work per Tick and the outstanding reliable window.
                for (var tile = 0; tile < TilesPerTick && (_control == null || _snapshotAcks.Count < TilesPerTick); tile++)
                {
                    if (!_fullSync.MoveNext()) { _fullSync.Dispose(); _fullSync = null; break; }
                    var message = _fullSync.Current;
                    if (_control != null) _snapshotAcks[message.opId] = now;
                    if (!_session.SendTerrain(message))
                    { SkippedDuringRecovery++; Fail("Terrain snapshot could not be sent."); return; }
                    FullSyncSent++; Sent++;
                    if (_failureRaised) return; // A synchronous rejection may have disposed the iterator.
                }
            }
            catch (Exception ex) { SkippedDuringRecovery++; Fail("Terrain snapshot failed: " + ex.Message); }
        }

        IEnumerable<WireMessage> FullSyncTiles(List<TerrainBaseline> terrains)
        {
            foreach (var b in terrains)
                foreach (var message in b.Snapshot(_fullSyncId)) yield return message;
        }

        void StopFullSync()
        {
            _fullSyncRequested = false;
            if (_fullSync != null) _fullSync.Dispose();
            _fullSync = null;
            _snapshotAcks.Clear();
        }

        void Skip(string reason)
        {
            Skipped++;
            if (_recovering) SkippedDuringRecovery++;
            if (_control == null) LastNote = reason;
            else Fail(reason);
        }

        void Fail(string reason)
        {
            LastNote = reason;
            _editingAllowed = false;
            _recoveryRequired = true;
            CancelEditing();
            StopFullSync();
            if (_failureRaised) return;
            _failureRaised = true;
            Failed?.Invoke(reason);
        }

        static string NewId() => System.Guid.NewGuid().ToString("N");

        static RectInt Map(int resolution, RectInt region)
        {
            if (!TerrainRegionRules.TryMapPatch(resolution, region.x, region.y, region.width, region.height,
                    out var x, out var z, out var width, out var height))
                throw new ArgumentException("Invalid terrain patch bounds.");
            return new RectInt(x, z, width, height);
        }

        void RefreshTerrainMap()
        {
            _active.Clear(); _components.Clear(); _byData.Clear();
            Unsupported = 0;
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded) return;
            _roots.Clear();
            scene.GetRootGameObjects(_roots);
            foreach (var root in _roots)
            {
                _componentBuffer.Clear();
                root.GetComponentsInChildren(true, _componentBuffer);
                foreach (var terrain in _componentBuffer)
                {
                    var data = terrain.terrainData;
                    if (data == null) continue;
                    _components.Add(terrain);
                    if (_byData.ContainsKey(data)) continue;
                    if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(data, out string guid, out long localId)
                        || string.IsNullOrEmpty(guid)) { Unsupported++; continue; }
                    if (!_baselines.TryGetValue(guid, out var baseline) || baseline.Data != data)
                    {
                        baseline = new TerrainBaseline(guid, data);
                        _baselines[guid] = baseline;
                        ObserveVersions(baseline);
                    }
                    _byData.Add(data, baseline);
                    _active.Add(baseline);
                }
            }
        }

        static uint AlphaVersion(TerrainData data)
        {
            uint version = 0;
            for (var i = 0; i < data.alphamapTextureCount; i++)
                unchecked { version = version * 31 + data.GetAlphamapTexture(i).updateCount; }
            return version;
        }

        static bool VersionsChanged(TerrainBaseline b)
        {
            return b.DirtyCount != EditorUtility.GetDirtyCount(b.Data)
                || b.HeightVersion != b.Data.heightmapTexture.updateCount || b.AlphaVersion != AlphaVersion(b.Data);
        }

        static void ObserveVersions(TerrainBaseline b)
        {
            if (b.Data == null) return;
            b.DirtyCount = EditorUtility.GetDirtyCount(b.Data);
            b.HeightVersion = b.Data.heightmapTexture.updateCount;
            b.AlphaVersion = AlphaVersion(b.Data);
        }

        void AuditUnreportedChanges()
        {
            // A bounded fallback for Undo and APIs which only dirty the asset/texture. Normal
            // brushes are captured by exact callback rectangles, never by repeated full reads.
            var budget = TilesPerTick;
            foreach (var b in _active)
            {
                if (!b.AuditRequested || !b.MatchesLayout) continue;
                var heightSide = (b.HeightResolution + TerrainPatchCodec.HeightTile - 1) / TerrainPatchCodec.HeightTile;
                var heightTiles = heightSide * heightSide;
                var alphaTile = TerrainPatchCodec.TileForLayers(b.Layers);
                var alphaSide = (b.AlphaResolution + alphaTile - 1) / alphaTile;
                var total = heightTiles + (b.Splat == null ? 0 : alphaSide * alphaSide);
                while (budget > 0 && b.AuditCursor < total)
                {
                    var height = b.AuditCursor < heightTiles;
                    var index = height ? b.AuditCursor : b.AuditCursor - heightTiles;
                    var side = height ? heightSide : alphaSide;
                    var tile = height ? TerrainPatchCodec.HeightTile : alphaTile;
                    var resolution = height ? b.HeightResolution : b.AlphaResolution;
                    var x = index % side * tile; var z = index / side * tile;
                    b.AuditCursor++; budget--;
                    try { HandleChanged(b, height ? TerrainPatchCodec.KindHeight : TerrainPatchCodec.KindSplat,
                        new RectInt(x, z, Math.Min(tile, resolution - x), Math.Min(tile, resolution - z))); }
                    catch (Exception ex) { Fail("Terrain audit failed: " + ex.Message); return; }
                }
                if (b.AuditCursor == total) { b.AuditCursor = 0; b.AuditRequested = false; ObserveVersions(b); }
                if (budget == 0) break;
            }
        }

        /// <summary>Retained benchmark seam; scans wire tiles and advances only this isolated test baseline.</summary>
        internal bool ScanForTest(TerrainData data, out int x, out int z, out int width, out int height)
        {
            if (_testBaseline == null || _testBaseline.Data != data || !_testBaseline.MatchesLayout)
                _testBaseline = new TerrainBaseline("__test__", data);
            var patches = _testBaseline.Capture(TerrainPatchCodec.KindHeight,
                new RectInt(0, 0, data.heightmapResolution, data.heightmapResolution), ref _tilesRead);
            x = z = width = height = 0;
            if (patches.Count == 0) return false;
            var minX = int.MaxValue; var minZ = int.MaxValue; var maxX = 0; var maxZ = 0;
            foreach (var patch in patches)
            {
                var area = patch.Area;
                minX = Math.Min(minX, area.x); minZ = Math.Min(minZ, area.y);
                maxX = Math.Max(maxX, area.xMax); maxZ = Math.Max(maxZ, area.yMax);
                _testBaseline.Commit(patch);
            }
            x = minX; z = minZ; width = maxX - minX; height = maxZ - minZ;
            return true;
        }
    }
}
