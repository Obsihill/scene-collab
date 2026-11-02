using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// The glue between one connection and one open scene: prime the id map, watch local edits, pace
    /// the outbox, apply remote ops.
    ///
    /// The outbox exists because the server accepts an op only when the sender's revision equals the
    /// authoritative one. Two ops in flight would make the second stale by construction, so exactly
    /// one op is in flight at a time and the next is sent when the ack (or rejection) arrives. Ops are
    /// full values, not deltas, so resending after a rejection is safe — and that is why a rejected op
    /// costs a round trip, not correctness.
    /// </summary>
    public sealed class SceneCollabSync : IDisposable
    {
        public const double TickIntervalSeconds = 0.1;

        readonly SceneNodeRegistry _registry = new SceneNodeRegistry();
        readonly SceneDiff _diff = new SceneDiff();
        readonly SceneChangeFeed _feed = new SceneChangeFeed();
        readonly SceneStructure _structure;
        readonly SceneOpApplier _applier;
        readonly Queue<SceneOperation> _outbox = new Queue<SceneOperation>();
        readonly Queue<WireMessage> _snapshotOutbox = new Queue<WireMessage>();
        readonly SceneViewPresence _presence = new SceneViewPresence();
        readonly TerrainSync _terrain = new TerrainSync();
        readonly List<GameObject> _dirty = new List<GameObject>();
        readonly SnapshotReceiver _snapshot = new SnapshotReceiver();
        readonly HashSet<string> _peersSeen = new HashSet<string>(StringComparer.Ordinal);

        ISceneCollabSession _session;
        double _nextTick;
        double _nextMarkerSweep;

        /// <summary>How often markers are re-checked against the content they stand in for.</summary>
        const double MarkerSweepSeconds = 1.0;
        bool _awaitingAck;
        bool _primed;
        bool _listening;
        bool _snapshotInFlight;
        bool _snapshotRequested;
        int _snapshotChunksSent;

        public SceneCollabSync()
        {
            _structure = new SceneStructure(_registry);
            _applier = new SceneOpApplier(_registry, _diff, _structure);

            // Reference fields travel as node ids, so the diff needs the registry that mints them.
            _diff.Registry = _registry;
            _feed.UndoRedoPerformed += OnUndoRedo;
            SnapshotDeletesExtras = true;
        }

        public bool Enabled { get; private set; }

        public int Sent { get; private set; }

        public int Applied
        {
            get { return _applier.Applied; }
        }

        public int Ignored
        {
            get { return _applier.Ignored; }
        }

        public int Overwritten
        {
            get { return _applier.Overwritten; }
        }

        /// <summary>
        /// References a peer sent that this machine could not resolve — a material, prefab or scene
        /// object it does not have. Shown in the window because each one is an ERROR box in the scene.
        /// </summary>
        public int MissingReferences
        {
            get { return _applier.MissingReferences; }
        }

        /// <summary>Values the codec could not express; each one is a field this editor silently lacks.</summary>
        public int UnsupportedValues
        {
            get { return _applier.UnsupportedValues; }
        }

        public int Rejected { get; private set; }

        public int Queued
        {
            get { return _outbox.Count; }
        }

        public int KnownNodes
        {
            get { return _registry.Count; }
        }

        public string LastNote { get; private set; }

        public SceneNodeRegistry Registry
        {
            get { return _registry; }
        }

        public SceneDiff Diff
        {
            get { return _diff; }
        }

        public SceneOpApplier Applier
        {
            get { return _applier; }
        }

        public SceneStructure Structure
        {
            get { return _structure; }
        }

        /// <summary>Structural ops (adds, removes, moves) sent this session.</summary>
        public int StructureSent { get; private set; }

        /// <summary>Structural ops held back because a peer holds the lock.</summary>
        public int LocksSkipped { get; private set; }

        /// <summary>Assets in the host's manifest that this machine cannot resolve.</summary>
        public int MissingAssets { get; private set; }

        public string AssetNote { get; private set; }

        /// <summary>Snapshot frames sent to the room this session (host side).</summary>
        public int SnapshotSent
        {
            get { return _snapshotChunksSent; }
        }

        /// <summary>True while a host snapshot is being applied (the local editor does not emit).</summary>
        public bool SnapshotActive
        {
            get { return _snapshot.Active; }
        }

        /// <summary>Nodes the last snapshot claimed (guest side).</summary>
        public int SnapshotNodes { get; private set; }

        /// <summary>Ops the last snapshot applied (guest side).</summary>
        public int SnapshotApplied
        {
            get { return _snapshot.Ops; }
        }

        /// <summary>Snapshots the room refused (stale revision, bad payload); the host logs and stops.</summary>
        public int SnapshotFailures { get; private set; }

        /// <summary>
        /// When true (default), a finished snapshot deletes objects this editor has that the host does
        /// not: the host's scene is the truth, and leftovers from an older session would otherwise drift
        /// forever.
        /// </summary>
        public bool SnapshotDeletesExtras { get; set; }

        /// <summary>Other editors' SceneView cameras, drawn as an editor overlay.</summary>
        public SceneViewPresence Presence
        {
            get { return _presence; }
        }

        /// <summary>
        /// Terrain patches (v0.5). Terrain lives in an asset, so it has its own channel; the counters
        /// here are what the window shows when a peer's terrain could not be applied.
        /// </summary>
        internal TerrainSync Terrain
        {
            get { return _terrain; }
        }

        public void Bind(ISceneCollabSession session)
        {
            if (ReferenceEquals(_session, session))
            {
                return;
            }

            Unbind();

            _session = session;
            if (_session == null)
            {
                return;
            }

            _session.OperationReceived += OnOperationReceived;
            _session.OperationAcknowledged += OnOperationAcknowledged;
            _session.StateChanged += OnStateChanged;
            _session.LocksChanged += OnLocksChanged;
            _session.AssetManifestChanged += OnAssetManifestChanged;
            _session.ParticipantsChanged += OnParticipantsChanged;
            _presence.Bind(_session);
            _terrain.Bind(_session);
        }

        public void Unbind()
        {
            if (_session != null)
            {
                _session.OperationReceived -= OnOperationReceived;
                _session.OperationAcknowledged -= OnOperationAcknowledged;
                _session.StateChanged -= OnStateChanged;
                _session.LocksChanged -= OnLocksChanged;
                _session.AssetManifestChanged -= OnAssetManifestChanged;
                _session.ParticipantsChanged -= OnParticipantsChanged;
            }

            _snapshotOutbox.Clear();
            _peersSeen.Clear();
            _snapshot.Abort();
            _snapshotInFlight = false;
            _presence.Unbind();
            _terrain.Unbind();
            _session = null;
        }

        /// <summary>Starts syncing the active scene. Idempotent.</summary>
        public void Enable()
        {
            if (Enabled)
            {
                return;
            }

            Enabled = true;
            _awaitingAck = false;
            _outbox.Clear();
            _snapshotOutbox.Clear();
            _snapshotRequested = false;
            _terrain.Reset();
            PrimeActiveScene();

            _feed.Attach();

            if (!_listening)
            {
                EditorApplication.update += Tick;
                EditorSceneManager.sceneSaved += OnSceneSaved;
                _listening = true;
            }

            UpdateSaveGuard();
            LastNote = "syncing";
        }

        public void Disable()
        {
            if (!Enabled && !_listening)
            {
                return;
            }

            Enabled = false;
            _outbox.Clear();
            _awaitingAck = false;
            SceneSaveGuard.BlockNonScribeSaves = false;

            if (_listening)
            {
                EditorApplication.update -= Tick;
                EditorSceneManager.sceneSaved -= OnSceneSaved;
                _listening = false;
            }

            _feed.Detach();
            _terrain.Reset();
            LastNote = "off";
        }

        public void Dispose()
        {
            Disable();
            Unbind();
            _feed.UndoRedoPerformed -= OnUndoRedo;
        }

        // ── local edits → ops ──────────────────────────────────────────────────

        /// <summary>
        /// Registers every object in the active scene and records its values as the baseline. Runs on
        /// enable and whenever a remote op names a node this editor has never seen.
        /// </summary>
        public void PrimeActiveScene()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            _registry.Load(SceneNodeRegistry.SidecarPathFor(scene));

            var primed = 0;
            foreach (var gameObject in SceneNodeRegistry.EnumerateSceneObjects(scene))
            {
                var nodeId = _registry.EnsureNodeId(gameObject);
                if (nodeId == null)
                {
                    continue;
                }

                _diff.Baseline(gameObject, nodeId);
                _structure.Baseline(gameObject, nodeId);
                primed++;
            }

            _primed = primed > 0;
            LastNote = "primed " + primed + " node(s)";
        }

        void OnUndoRedo()
        {
            // Undo can restore any tracked value, and the feed only reports what Undo recorded, so the
            // safe response is to re-read everything the map knows about.
            var scene = SceneManager.GetActiveScene();
            foreach (var gameObject in SceneNodeRegistry.EnumerateSceneObjects(scene))
            {
                var nodeId = _registry.EnsureNodeId(gameObject);
                if (nodeId != null)
                {
                    _diff.Collect(gameObject, nodeId);
                    _structure.Baseline(gameObject, nodeId);
                }
            }
        }

        /// <summary>
        /// Swaps an untouched stand-in for the real prefab once its asset arrives, keeping the node id, the
        /// host's transform and the hierarchy position. Without this, retiring the marker would leave an
        /// empty object behind — quiet instead of red, and still not what the host has.
        /// </summary>
        void RestorePrefabStandIn(GameObject standIn, string prefabGuid)
        {
            if (standIn == null)
            {
                return;
            }

            var path = AssetDatabase.GUIDToAssetPath(prefabGuid);
            var prefab = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                return;
            }

            var parent = standIn.transform.parent;
            var position = standIn.transform.localPosition;
            var rotation = standIn.transform.localRotation;
            var scale = standIn.transform.localScale;
            var siblingIndex = standIn.transform.GetSiblingIndex();
            var name = standIn.name;
            var active = standIn.activeSelf;
            var scene = standIn.scene;

            string nodeId;
            _registry.TryGetNodeId(standIn, out nodeId);

            var created = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
            if (created == null)
            {
                return;
            }

            created.name = name;
            created.transform.SetParent(parent, false);
            created.transform.localPosition = position;
            created.transform.localRotation = rotation;
            created.transform.localScale = scale;
            created.transform.SetSiblingIndex(siblingIndex);
            created.SetActive(active);

            _registry.Forget(standIn);
            if (!string.IsNullOrEmpty(nodeId))
            {
                _registry.Adopt(created, nodeId);
                _diff.Collect(created, nodeId);
            }

            UnityEngine.Object.DestroyImmediate(standIn);

            UnityEngine.Debug.Log("Scene Collab: the prefab arrived, so the stand-in for \"" + name
                + "\" was replaced with " + path + ".");
        }

        void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;

            // Retire stale ERROR markers even when disconnected: one hung up while an asset was importing
            // outlives the connection, and it lies about a checkout that has already caught up. Walks the
            // marker list, not the scene, so it can run on a slow clock.
            if (now >= _nextMarkerSweep)
            {
                _nextMarkerSweep = now + MarkerSweepSeconds;
                CollabPlaceholders.Revalidate(RestorePrefabStandIn);
            }

            if (_session == null || _session.State != CollabConnectionState.Connected)
            {
                return;
            }

            if (now < _nextTick)
            {
                return;
            }

            _nextTick = now + TickIntervalSeconds;

            // Camera presence runs on the same clock but is not gated by a primed scene: a guest can see
            // where the host is looking before it has a single node id.
            _presence.Tick();

            // Terrain is likewise independent of the node graph: it is an asset, not a node, and a
            // room whose scene is still unprimed can already sculpt the same terrain.
            _terrain.Tick();

            if (!_primed)
            {
                PrimeActiveScene();
            }

            if (_snapshot.Active)
            {
                // A snapshot is landing. Emitting local diffs now would race the baseline we are
                // rebuilding op by op, and every value the snapshot writes would look like a local edit.
                _feed.Drain(_dirty);
                _dirty.Clear();
                PumpOutbox();
                return;
            }

            _dirty.Clear();
            var structureDirty = _feed.Drain(_dirty);

            for (var i = 0; i < _dirty.Count; i++)
            {
                var gameObject = _dirty[i];
                if (gameObject == null)
                {
                    continue;
                }

                var nodeId = _registry.EnsureNodeId(gameObject);
                if (nodeId == null)
                {
                    // Brand-new object in an unsaved scene: no stable id yet. It joins the session
                    // after the scribe saves, which is the documented v0.2 limit.
                    continue;
                }

                if (BlockedByLock(nodeId))
                {
                    continue;
                }

                var operations = _diff.Collect(gameObject, nodeId);
                for (var j = 0; j < operations.Count; j++)
                {
                    _outbox.Enqueue(operations[j]);
                }
            }

            if (structureDirty)
            {
                CollectStructure();
            }

            PumpOutbox();
        }

        /// <summary>
        /// Turns structural changes into ops, in an order a peer can apply without referring to
        /// something it does not have yet: removals, then creations (the hierarchy walk always visits a
        /// parent before its children), then moves.
        /// </summary>
        public void CollectStructure()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return;
            }

            var removed = _structure.DetectRemoved();
            for (var i = 0; i < removed.Count; i++)
            {
                var nodeId = removed[i];
                if (BlockedByLock(nodeId))
                {
                    continue;
                }

                _diff.Forget(nodeId);
                var removeOp = _structure.BuildRemove(nodeId, "");
                _outbox.Enqueue(removeOp);
                StructureSent++;
                LastNote = "removed node " + nodeId;
            }

            var added = _structure.DetectUnregistered(scene);
            for (var i = 0; i < added.Count; i++)
            {
                var gameObject = added[i];
                if (gameObject == null)
                {
                    continue;
                }

                var nodeId = SceneStructure.NewNodeId();
                bool created;
                var addOp = _structure.BuildAdd(gameObject, nodeId, out created);
                if (!created)
                {
                    // The parent is not in the session yet; the next sweep picks this object up.
                    continue;
                }

                _diff.Baseline(gameObject, nodeId);
                _outbox.Enqueue(addOp);
                StructureSent++;
                LastNote = "added " + gameObject.name;
            }

            foreach (var gameObject in SceneNodeRegistry.EnumerateSceneObjects(scene))
            {
                if (gameObject == null)
                {
                    continue;
                }

                string nodeId;
                if (!_registry.TryGetNodeId(gameObject, out nodeId) || string.IsNullOrEmpty(nodeId))
                {
                    continue;
                }

                if (BlockedByLock(nodeId))
                {
                    continue;
                }

                SceneOperation moveOp;
                if (_structure.TryCollectMove(gameObject, nodeId, out moveOp))
                {
                    _outbox.Enqueue(moveOp);
                    StructureSent++;
                    LastNote = "moved " + gameObject.name;
                }

                var componentOps = _structure.CollectComponentChanges(gameObject, nodeId);
                var addedComponents = false;
                for (var c = 0; c < componentOps.Count; c++)
                {
                    _outbox.Enqueue(componentOps[c]);
                    StructureSent++;
                    LastNote = componentOps[c].Kind + " " + componentOps[c].ComponentType
                        + " on " + gameObject.name;

                    if (componentOps[c].Kind == OpKinds.AddComponent)
                    {
                        addedComponents = true;
                    }
                }

                if (addedComponents)
                {
                    // The peer just built the component with its type defaults. Everything the sender
                    // holds for this object follows as explicit values, in the same breath as the add —
                    // waiting for the next diff would let the peer adopt the defaults as baseline and
                    // never receive the real ones.
                    var values = _diff.CollectAll(gameObject, nodeId);
                    for (var v = 0; v < values.Count; v++)
                    {
                        _outbox.Enqueue(values[v]);
                    }
                }
            }
        }

        bool BlockedByLock(string nodeId)
        {
            if (_session == null || string.IsNullOrEmpty(nodeId) || !_session.IsLockedByOther(nodeId))
            {
                return false;
            }

            LocksSkipped++;
            LastNote = "locked by a peer: " + nodeId;
            return true;
        }

        /// <summary>Host only: tell the room which assets the scene needs.</summary>
        public bool PublishManifest()
        {
            if (_session == null || _session.State != CollabConnectionState.Connected || !_session.IsHost)
            {
                return false;
            }

            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || string.IsNullOrEmpty(scene.path))
            {
                return false;
            }

            var guids = SceneIdentity.CollectAssetGuids(scene.path, 4096);
            if (!_session.PublishAssets(guids))
            {
                return false;
            }

            LastNote = "published " + guids.Length + " asset guid(s)";
            return true;
        }

        /// <summary>Any client: compare the manifest against this machine and stand in placeholders.</summary>
        public void RefreshAssetHealth()
        {
            if (_session == null || _session.AssetGuids == null || _session.AssetGuids.Length == 0)
            {
                return;
            }

            var missing = SceneIdentity.Missing(_session.AssetGuids);
            MissingAssets = missing.Length;

            var scene = SceneManager.GetActiveScene();
            var health = CollabAssetHealth.Scan(scene, missing);
            AssetNote = CollabPlaceholders.Describe(missing.Length, health.MissingScripts);
            LastNote = AssetNote ?? (MissingAssets + " missing asset(s)");
        }

        void PumpOutbox()
        {
            if (_awaitingAck)
            {
                return;
            }

            if (_session == null || _session.State != CollabConnectionState.Connected)
            {
                return;
            }

            if (_snapshotOutbox.Count > 0)
            {
                var frame = _snapshotOutbox.Dequeue();
                if (!_session.SendSnapshotFrame(frame))
                {
                    _snapshotOutbox.Clear();
                    LastNote = "not connected; snapshot dropped";
                    return;
                }

                _snapshotInFlight = true;
                _awaitingAck = true;
                if (frame.opKind == OpKinds.SnapshotChunk)
                {
                    _snapshotChunksSent++;
                }

                LastNote = "snapshot " + frame.opKind + " (" + _snapshotOutbox.Count + " left)";
                return;
            }

            if (_outbox.Count == 0)
            {
                return;
            }

            var operation = _outbox.Dequeue();
            if (!_session.SendOperation(operation))
            {
                LastNote = "not connected; dropped " + operation;
                return;
            }

            _awaitingAck = true;
            Sent++;
            LastNote = "sent " + operation;
        }

        // ── remote ops → scene ────────────────────────────────────────────────

        void OnOperationReceived(WireMessage message)
        {
            if (message == null)
            {
                return;
            }

            switch (message.opKind)
            {
                case OpKinds.SnapshotStart:
                    BeginSnapshot(message);
                    return;

                case OpKinds.SnapshotChunk:
                    ApplySnapshotChunk(message);
                    return;

                case OpKinds.SnapshotEnd:
                    FinishSnapshot(message);
                    return;

                case OpKinds.SnapshotRequest:
                    OnSnapshotRequested(message);
                    return;
            }

            string note;
            if (!_applier.Apply(message, out note) && note != null)
            {
                if (note.StartsWith("unknown node", StringComparison.Ordinal) && !_primed)
                {
                    // The op beat our baseline (fresh connect). Prime once and retry.
                    PrimeActiveScene();
                    if (_applier.Apply(message, out note))
                    {
                        LastNote = "applied " + message.opKind + " " + message.propertyPath;
                        return;
                    }
                }

                LastNote = note;
                return;
            }

            LastNote = "applied " + message.propertyPath + " from " + (message.displayName ?? "peer");
        }

        void OnOperationAcknowledged(WireMessage message)
        {
            _awaitingAck = false;

            if (_snapshotInFlight)
            {
                _snapshotInFlight = false;

                if (message != null && !message.accepted && !message.duplicate)
                {
                    // A rejected frame means the room moved on (stale revision) or the payload was
                    // refused. Retrying would rebuild a scene against a revision that has changed again,
                    // so the snapshot is abandoned and reported; the next request starts clean.
                    _snapshotOutbox.Clear();
                    SnapshotFailures++;
                    LastNote = "snapshot aborted: " + (message.code ?? "unknown");
                }

                PumpOutbox();
                return;
            }

            if (message != null && !message.accepted && !message.duplicate)
            {
                Rejected++;
                LastNote = "rejected: " + (message.code ?? "unknown");

                // Do not retry blindly: the next diff produces the current value against the new
                // revision, which is exactly what the server wants.
                _outbox.Clear();
            }

            PumpOutbox();
        }

        void OnStateChanged()
        {
            UpdateSaveGuard();

            if (_session != null && _session.State != CollabConnectionState.Connected)
            {
                // One request per connection. A guest that comes back after a drop is a new join as far
                // as the server is concerned — it has no baseline any more, so the flag has to clear or
                // the re-request is silently skipped and the editor carries on against a stale scene.
                _snapshotRequested = false;
            }

            if (_session != null && _session.State == CollabConnectionState.Connected && Enabled && !_primed)
            {
                PrimeActiveScene();
            }

            if (_session != null && _session.State == CollabConnectionState.Connected && Enabled && _session.IsHost)
            {
                PublishManifest();
            }

            if (_session != null && _session.State == CollabConnectionState.Connected && Enabled && !_session.IsHost)
            {
                // A guest that just joined has an empty registry: it cannot know what it is missing until
                // the host sends a snapshot, so ask as soon as the socket is usable.
                RequestSnapshot();
            }
        }

        // ── snapshot: host → late joiner ──────────────────────────────────────

        void OnParticipantsChanged()
        {
            if (_session == null || !Enabled)
            {
                return;
            }

            var current = new HashSet<string>(StringComparer.Ordinal);
            var joined = false;

            var participants = _session.Participants;
            for (var i = 0; i < participants.Count; i++)
            {
                var participant = participants[i];
                if (participant == null || string.IsNullOrEmpty(participant.clientId))
                {
                    continue;
                }

                current.Add(participant.clientId);

                if (!string.Equals(participant.clientId, _session.ClientId, StringComparison.Ordinal)
                    && _peersSeen.Add(participant.clientId))
                {
                    joined = true;
                }
            }

            // Drop departed peers so a rejoin is treated as new again.
            _peersSeen.IntersectWith(current);

            if (!joined)
            {
                return;
            }

            if (_session.IsHost)
            {
                QueueSnapshot("a peer joined");
            }
            else
            {
                // Someone arrived, or this editor is the one that just came back: either way asking is
                // cheaper than guessing whether the local scene is current.
                RequestSnapshot();
            }
        }

        /// <summary>Guest side: tell the host this editor has nothing and wants a baseline.</summary>
        public void RequestSnapshot()
        {
            if (_session == null || _session.State != CollabConnectionState.Connected || _snapshotRequested)
            {
                return;
            }

            _snapshotRequested = true;
            _snapshotOutbox.Enqueue(new WireMessage
            {
                snapshot = true,
                opKind = OpKinds.SnapshotRequest,
                propertyPath = "",
                oldValue = "",
                newValue = "",
            });

            LastNote = "asked the host for a snapshot";
        }

        /// <summary>Host side: build the whole scene into frames and queue them one ack at a time.</summary>
        public bool QueueSnapshot(string reason)
        {
            if (_session == null || _session.State != CollabConnectionState.Connected)
            {
                return false;
            }

            if (_snapshotOutbox.Count > 0)
            {
                // A snapshot is already on its way; a second one would race it into a half-applied scene.
                LastNote = "snapshot already queued";
                return false;
            }

            List<WireMessage> frames;
            string note;
            if (!SceneSnapshot.TryBuild(SceneManager.GetActiveScene(), _registry, out frames, out note))
            {
                LastNote = "snapshot skipped: " + note;
                return false;
            }

            for (var i = 0; i < frames.Count; i++)
            {
                _snapshotOutbox.Enqueue(frames[i]);
            }

            LastNote = "snapshot queued (" + reason + "): " + note;
            return true;
        }

        void BeginSnapshot(WireMessage message)
        {
            if (_session != null && string.Equals(message.clientId, _session.ClientId, StringComparison.Ordinal))
            {
                return;
            }

            _snapshot.Begin();
            LastNote = "snapshot from " + (string.IsNullOrEmpty(message.displayName) ? "host" : message.displayName) + " — receiving";
        }

        void ApplySnapshotChunk(WireMessage message)
        {
            if (!_snapshot.Active)
            {
                return;
            }

            if (!_snapshot.ApplyChunk(message.newValue, _applier))
            {
                LastNote = "snapshot: " + (_snapshot.LastError ?? "unreadable chunk");
                _snapshot.Abort();
                return;
            }

            LastNote = "snapshot " + _snapshot.Chunks + "/" + _snapshot.TotalChunks
                + " — " + _snapshot.Ops + " op(s)";
        }

        void FinishSnapshot(WireMessage message)
        {
            if (!_snapshot.Active)
            {
                return;
            }

            var claimed = _snapshot.End();
            var ops = _snapshot.Ops;
            var removed = RemoveExtras(claimed);

            // The applier wrote every value, so a fresh baseline is what keeps those writes from looking
            // like local edits on the next tick.
            PrimeActiveScene();

            SnapshotNodes = claimed.Count;
            LastNote = "snapshot done: " + claimed.Count + " node(s), " + ops + " op(s), " + removed + " removed";
        }

        int RemoveExtras(HashSet<string> claimed)
        {
            if (!SnapshotDeletesExtras)
            {
                return 0;
            }

            var scene = SceneManager.GetActiveScene();
            var extras = new List<GameObject>();
            var extraIds = new List<string>();

            foreach (var gameObject in SceneNodeRegistry.EnumerateSceneObjects(scene))
            {
                if (gameObject == null)
                {
                    continue;
                }

                // A child of a prefab instance cannot be deleted on its own, and the host's prefab is the
                // same asset: its presence is not drift.
                if (PrefabUtility.IsPartOfPrefabInstance(gameObject)
                    && !PrefabUtility.IsOutermostPrefabInstanceRoot(gameObject))
                {
                    continue;
                }

                string nodeId;
                if (!_registry.TryGetNodeId(gameObject, out nodeId) || string.IsNullOrEmpty(nodeId))
                {
                    continue;
                }

                if (claimed.Contains(nodeId))
                {
                    continue;
                }

                extras.Add(gameObject);
                extraIds.Add(nodeId);
            }

            var removed = 0;
            for (var i = 0; i < extras.Count; i++)
            {
                var gameObject = extras[i];
                if (gameObject == null)
                {
                    continue;
                }

                _structure.Forget(extraIds[i]);
                _diff.Forget(extraIds[i]);
                _registry.Forget(gameObject);
                UnityEngine.Object.DestroyImmediate(gameObject);
                removed++;
            }

            return removed;
        }

        void OnSnapshotRequested(WireMessage message)
        {
            if (_session == null || !Enabled || !_session.IsHost)
            {
                return;
            }

            var who = string.IsNullOrEmpty(message.displayName)
                ? (string.IsNullOrEmpty(message.clientId) ? "a guest" : message.clientId)
                : message.displayName;

            QueueSnapshot(who + " asked");
        }

        void OnLocksChanged()
        {
            LastNote = "locks: " + (_session == null ? 0 : _session.LockedNodeIds.Length);
        }

        void OnAssetManifestChanged()
        {
            RefreshAssetHealth();
        }

        void UpdateSaveGuard()
        {
            SceneSaveGuard.BlockNonScribeSaves =
                Enabled && _session != null
                && _session.State == CollabConnectionState.Connected
                && !_session.IsHost;
        }

        void OnSceneSaved(Scene scene)
        {
            if (!Enabled)
            {
                return;
            }

            var sidecar = SceneNodeRegistry.SidecarPathFor(scene);
            if (sidecar != null)
            {
                _registry.Save(sidecar, scene);
                LastNote = "wrote " + sidecar;
            }
        }
    }
}
