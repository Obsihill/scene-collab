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
        readonly List<GameObject> _dirty = new List<GameObject>();

        ISceneCollabSession _session;
        double _nextTick;
        bool _awaitingAck;
        bool _primed;
        bool _listening;

        public SceneCollabSync()
        {
            _structure = new SceneStructure(_registry);
            _applier = new SceneOpApplier(_registry, _diff, _structure);
            _feed.UndoRedoPerformed += OnUndoRedo;
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
            }

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

        void Tick()
        {
            if (!Enabled)
            {
                return;
            }

            if (_session == null || _session.State != CollabConnectionState.Connected)
            {
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            if (now < _nextTick)
            {
                return;
            }

            _nextTick = now + TickIntervalSeconds;

            if (!_primed)
            {
                PrimeActiveScene();
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
                for (var c = 0; c < componentOps.Count; c++)
                {
                    _outbox.Enqueue(componentOps[c]);
                    StructureSent++;
                    LastNote = componentOps[c].Kind + " " + componentOps[c].ComponentType
                        + " on " + gameObject.name;
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
            if (_awaitingAck || _outbox.Count == 0)
            {
                return;
            }

            if (_session == null || _session.State != CollabConnectionState.Connected)
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

            if (_session != null && _session.State == CollabConnectionState.Connected && Enabled && !_primed)
            {
                PrimeActiveScene();
            }

            if (_session != null && _session.State == CollabConnectionState.Connected && Enabled && _session.IsHost)
            {
                PublishManifest();
            }
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
