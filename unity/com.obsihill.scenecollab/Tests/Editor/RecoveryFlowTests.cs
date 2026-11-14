using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using SceneCollab.Editor.SceneSync;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// Exercises the real sync owner and snapshot receiver through a small in-memory transport.
    /// Only the editor update/outbox scheduling seams are invoked directly; recovery state is never
    /// set by reflection, and no server, saved scene, window or fake Unity editor is needed.
    /// </summary>
    public sealed class RecoveryFlowTests
    {
        const string Epoch = "recovery-1";
        const string NextEpoch = "recovery-2";
        const string NodeId = "0123456789abcdef";
        const string RecoveredName = "Recovered object";
        Scene _scene;
        SceneCollabSync _sync;
        RecoverySession _session;
        GameObject _sentinel;
        HideFlags _sentinelFlags, _sentinelTransformFlags;

        [SetUp]
        public void SetUp()
        {
            _sync = null;
            _scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            _sentinel = new GameObject("Pause sentinel");
            _sentinel.hideFlags = HideFlags.HideInInspector;
            _sentinelFlags = _sentinel.hideFlags;
            _sentinelTransformFlags = _sentinel.transform.hideFlags;
        }

        [TearDown]
        public void TearDown()
        {
            _sync?.Dispose();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        void Start(bool host = false)
        {
            _session = new RecoverySession { IsHost = host };
            // Keep the sentinel so every recovery stage can assert the owner actually applies the gate.
            _sync = new SceneCollabSync { SnapshotDeletesExtras = false };
            _sync.Bind(_session);
            _sync.Enable();
        }

        void AssertPaused()
        {
            Assert.IsFalse(_sync.CanEdit);
            Assert.IsTrue(SceneSaveGuard.RecoveryBlocked);
            Assert.AreEqual(_sentinelFlags | HideFlags.NotEditable, _sentinel.hideFlags);
            Assert.AreEqual(_sentinelTransformFlags | HideFlags.NotEditable, _sentinel.transform.hideFlags);
        }

        void Begin(string epoch = Epoch)
        {
            _session.DeliverControl(new WireMessage { type = MessageTypes.SyncBegin, syncId = epoch });
        }

        void End(string epoch = Epoch, int terrainTiles = 0)
        {
            _session.DeliverControl(new WireMessage
            {
                type = MessageTypes.SyncEnd, syncId = epoch, syncTerrainTiles = terrainTiles,
            });
        }

        void Ready(string epoch = Epoch, bool accepted = true)
        {
            _session.DeliverControl(new WireMessage
            {
                type = MessageTypes.SyncReady, syncId = epoch, accepted = accepted,
                code = accepted ? null : "recovery_failed", message = accepted ? null : "a peer failed",
            });
        }

        void SnapshotFrame(string kind, string epoch, string payload = null)
        {
            _session.DeliverOperation(new WireMessage
            {
                type = MessageTypes.Op, opKind = kind, clientId = "host", syncId = epoch,
                snapshot = true, opId = Guid.NewGuid().ToString("N"), newValue = payload,
            });
        }

        void SnapshotBody(string epoch = Epoch, int totalChunks = 1)
        {
            SnapshotFrame(OpKinds.SnapshotStart, epoch);
            SnapshotFrame(OpKinds.SnapshotChunk, epoch, JsonUtility.ToJson(new SnapshotChunk
            {
                index = 0,
                total = totalChunks,
                ops = new[]
                {
                    new WireMessage
                    {
                        opKind = OpKinds.AddNode, nodeId = NodeId,
                        newValue = JsonUtility.ToJson(new SceneStructure.AddNodePayload
                        {
                            name = RecoveredName, active = true, rw = 1, sx = 1, sy = 1, sz = 1,
                        }),
                    },
                },
            }));
        }

        GameObject CompleteScene(string epoch = Epoch)
        {
            SnapshotBody(epoch);
            SnapshotFrame(OpKinds.SnapshotEnd, epoch);
            GameObject recovered;
            Assert.IsTrue(_sync.Registry.TryResolve(NodeId, out recovered), "The real snapshot must create the node.");
            Assert.AreEqual(1, _sync.SnapshotNodes);
            Assert.IsFalse(_sync.SnapshotActive);
            Assert.AreEqual(RecoveryPhase.Terrain, _sync.RecoveryState);
            return recovered;
        }

        GameObject BecomeReady(string epoch = Epoch)
        {
            Begin(epoch);
            var recovered = CompleteScene(epoch);
            End(epoch);
            Ready(epoch);
            Assert.IsTrue(_sync.CanEdit);
            return recovered;
        }

        void Invoke(string method, params object[] arguments)
        {
            var callback = typeof(SceneCollabSync).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(callback, "Missing editor scheduling seam: " + method);
            callback.Invoke(_sync, arguments);
        }

        void QueueNameChange(GameObject target, string name)
        {
            string before, after;
            Assert.IsTrue(ScenePropertyAccess.TryRead(target, ScenePropertyAccess.Name, out before));
            target.name = name;
            Assert.IsTrue(ScenePropertyAccess.TryRead(target, ScenePropertyAccess.Name, out after));
            Invoke("EnqueueLocal", new SceneOperation
            {
                Kind = OpKinds.SetProperty, NodeId = NodeId, PropertyPath = ScenePropertyAccess.Name,
                OldValue = before, NewValue = after,
            });
            Invoke("PumpOutbox");
        }

        void PeerRename(string before, string after)
        {
            _session.DeliverOperation(new WireMessage
            {
                type = MessageTypes.Op, opKind = OpKinds.SetProperty, clientId = "peer",
                opId = Guid.NewGuid().ToString("N"), nodeId = NodeId,
                propertyPath = ScenePropertyAccess.Name, oldValue = before, newValue = after,
            });
        }

        [Test]
        public void EditingStaysPausedUntilSceneEndValidatedSyncEndAndSameEpochReady()
        {
            Start();
            Assert.AreEqual(RecoveryPhase.Requesting, _sync.RecoveryState);
            Assert.AreEqual(MessageTypes.SyncRequest, _session.SentControls[0].type);
            AssertPaused();
            Ready(); // A welcome or unsolicited ready cannot replace the recovery exchange.
            AssertPaused();

            Begin();
            Assert.AreEqual(RecoveryPhase.Scene, _sync.RecoveryState);
            Ready();
            AssertPaused();
            SnapshotBody();
            Assert.IsTrue(_sync.SnapshotActive);
            Ready();
            AssertPaused();

            SnapshotFrame(OpKinds.SnapshotEnd, Epoch);
            Assert.AreEqual(RecoveryPhase.Terrain, _sync.RecoveryState);
            Assert.AreEqual(1, _sync.SnapshotNodes);
            Invoke("Tick");
            GameObject recovered;
            Assert.IsTrue(_sync.Registry.TryResolve(NodeId, out recovered));
            Assert.AreEqual(HideFlags.NotEditable, recovered.hideFlags, "Tick must guard snapshot-created objects.");
            Ready();
            AssertPaused();

            End();
            Assert.AreEqual(RecoveryPhase.Verifying, _sync.RecoveryState);
            var receipt = _session.SentControls.FindLast(message => message.type == MessageTypes.SyncReady);
            Assert.IsNotNull(receipt);
            Assert.AreEqual(Epoch, receipt.syncId);
            Assert.IsTrue(receipt.accepted);
            AssertPaused(); // Sending our receipt is not the server's all-peers-ready verdict.

            Ready();
            Assert.IsTrue(_sync.CanEdit);
            Assert.AreEqual(RecoveryPhase.Ready, _sync.RecoveryState);
            Assert.IsFalse(SceneSaveGuard.RecoveryBlocked);
            Assert.AreEqual(_sentinelFlags, _sentinel.hideFlags);
            Assert.AreEqual(_sentinelTransformFlags, _sentinel.transform.hideFlags);
            Assert.AreEqual(HideFlags.None, recovered.hideFlags);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SyncEndBeforeSceneCompletionReportsFailureAndCannotBeOverriddenByReady(bool receivedBody)
        {
            Start();
            Begin();
            if (receivedBody) SnapshotBody(); // All chunks arrived, but snapshot_end did not.

            End();

            Assert.AreEqual(RecoveryPhase.Failed, _sync.RecoveryState);
            var receipt = _session.SentControls.FindLast(message => message.type == MessageTypes.SyncReady);
            Assert.IsNotNull(receipt);
            Assert.AreEqual(Epoch, receipt.syncId);
            Assert.IsFalse(receipt.accepted);
            Assert.IsNotEmpty(_sync.LastFailure);
            Ready();
            Assert.AreEqual(RecoveryPhase.Failed, _sync.RecoveryState);
            AssertPaused();
        }

        [Test]
        public void SnapshotEndWithAMissingChunkCannotSatisfyRecovery()
        {
            Start();
            Begin();
            SnapshotBody(totalChunks: 2);

            SnapshotFrame(OpKinds.SnapshotEnd, Epoch);
            End();
            Ready();

            Assert.AreEqual(RecoveryPhase.Failed, _sync.RecoveryState);
            Assert.IsFalse(_sync.SnapshotActive);
            Assert.IsFalse(_session.SentControls.FindLast(message => message.type == MessageTypes.SyncReady).accepted);
            AssertPaused();
        }

        [Test]
        public void MissingTerrainTilesFailValidationEvenWithACompleteScene()
        {
            Start();
            Begin();
            CompleteScene();

            End(terrainTiles: 1); // This scene has no terrain, so zero tiles have actually arrived.
            Ready();

            Assert.AreEqual(RecoveryPhase.Failed, _sync.RecoveryState);
            Assert.IsFalse(_session.SentControls.FindLast(message => message.type == MessageTypes.SyncReady).accepted);
            AssertPaused();
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("unrelated-epoch")]
        [TestCase(Epoch)]
        public void ReadyFromAnotherEpochIsIgnoredEvenAfterCurrentValidation(string wrongEpoch)
        {
            Start();
            Begin();
            CompleteScene();
            End(); // The previous epoch was eligible, but was superseded before its ready arrived.
            Begin(NextEpoch);
            CompleteScene(NextEpoch);
            End(NextEpoch);

            Ready(wrongEpoch);
            Ready(wrongEpoch, false); // A stale rejection must not fail the current exchange either.

            Assert.AreEqual(RecoveryPhase.Verifying, _sync.RecoveryState);
            AssertPaused();
            Ready(NextEpoch);
            Assert.IsTrue(_sync.CanEdit);
        }

        [Test]
        public void WrongEpochSyncEndCannotValidateTheCurrentScene()
        {
            Start();
            Begin(NextEpoch);
            CompleteScene(NextEpoch);
            var sent = _session.SentControls.Count;

            End(Epoch);
            Ready(NextEpoch);

            Assert.AreEqual(sent, _session.SentControls.Count, "Ignore the stale barrier without sending a receipt.");
            Assert.AreEqual(RecoveryPhase.Terrain, _sync.RecoveryState);
            AssertPaused();
            End(NextEpoch);
            Ready(NextEpoch);
            Assert.IsTrue(_sync.CanEdit);
        }

        [Test]
        public void ANewEpochDiscardsPreviousSceneCompletionAndReadyEligibility()
        {
            Start();
            Begin();
            CompleteScene();
            End();
            Begin(NextEpoch);

            Ready();
            Ready(NextEpoch);
            SnapshotBody(Epoch);
            SnapshotFrame(OpKinds.SnapshotEnd, Epoch);

            Assert.AreEqual(RecoveryPhase.Scene, _sync.RecoveryState);
            Assert.IsFalse(_sync.SnapshotActive, "Old snapshot frames must not open the new epoch's receiver.");
            AssertPaused();
            CompleteScene(NextEpoch);
            Ready(NextEpoch);
            AssertPaused();
            End(NextEpoch);
            Ready(NextEpoch);
            Assert.IsTrue(_sync.CanEdit);
        }

        [Test]
        public void GuestPromotedDuringAPartialSnapshotCannotPublishItAsANewRecoveryBaseline()
        {
            Start();
            _session.ChangeState(CollabConnectionState.Connected);
            Begin();
            SnapshotBody(totalChunks: 2);
            Assert.IsTrue(_sync.SnapshotActive);
            Assert.AreEqual(1, _sync.RecoverySceneCompleted);
            Assert.AreEqual(2, _sync.RecoverySceneTotal);
            GameObject partial;
            Assert.IsTrue(_sync.Registry.TryResolve(NodeId, out partial), "Part of the old host's scene has already landed.");

            _session.IsHost = true;
            _session.ChangeState(CollabConnectionState.Connected);
            AssertPaused();
            Begin(NextEpoch);
            Invoke("PumpOutbox");

            Assert.AreEqual(RecoveryPhase.Failed, _sync.RecoveryState);
            Assert.IsNotEmpty(_sync.LastFailure);
            Assert.IsEmpty(_session.SentSnapshots, "Promotion must not turn a partial copy into the room's baseline.");
            Assert.IsFalse(_sync.SnapshotActive);
            Assert.IsFalse(_sync.WaitingForAck);
            Assert.IsFalse(_sync.Terrain.FullSyncInProgress);
            AssertPaused();
            Ready(NextEpoch);
            Invoke("PumpOutbox");
            Assert.AreEqual(RecoveryPhase.Failed, _sync.RecoveryState);
            Assert.IsEmpty(_session.SentSnapshots);
            _sync.Disable();
            _sync.Enable();
            Begin("recovery-after-reenable");
            Invoke("PumpOutbox");
            Assert.AreEqual(RecoveryPhase.Failed, _sync.RecoveryState);
            Assert.IsEmpty(_session.SentSnapshots, "Toggling sync cannot certify an incomplete promoted host.");
        }

        [Test]
        public void ARejectedReadyKeepsEditingBlockedDespiteLocalValidation()
        {
            Start();
            Begin();
            CompleteScene();
            End();

            Ready(accepted: false);
            Ready();

            Assert.AreEqual(RecoveryPhase.Failed, _sync.RecoveryState);
            StringAssert.Contains("a peer failed", _sync.LastFailure);
            AssertPaused();
        }

        [Test]
        public void DisconnectClearsSnapshotAckWaitAndOldAckCannotReleaseTheNewEpochsWait()
        {
            Start(host: true);
            Begin();
            Invoke("PumpOutbox");
            Assert.IsTrue(_sync.WaitingForAck);
            Assert.AreEqual(1, _session.SentSnapshots.Count);
            var oldFrame = _session.SentSnapshots[0];
            Assert.AreEqual(OpKinds.SnapshotStart, oldFrame.opKind);

            _session.Disconnect();

            Assert.IsFalse(_sync.WaitingForAck);
            Assert.AreEqual(RecoveryPhase.Disconnected, _sync.RecoveryState);
            AssertPaused();
            var addedOffline = new GameObject("Created while disconnected");
            Invoke("Tick");
            Assert.AreEqual(HideFlags.NotEditable, addedOffline.hideFlags);
            Assert.AreEqual(HideFlags.NotEditable, addedOffline.transform.hideFlags);
            Invoke("PumpOutbox");
            _session.Acknowledge(oldFrame);
            Assert.IsFalse(_sync.WaitingForAck);

            _session.ChangeState(CollabConnectionState.Connected);
            Invoke("PumpOutbox");
            Assert.AreEqual(1, _session.SentSnapshots.Count, "A disconnect must discard queued snapshot frames.");
            AssertPaused();
            Begin(NextEpoch);
            Invoke("PumpOutbox");
            var newFrame = _session.SentSnapshots[1];
            Assert.AreEqual(NextEpoch, newFrame.syncId);
            Assert.AreNotEqual(oldFrame.opId, newFrame.opId);

            _session.Acknowledge(oldFrame);

            Assert.IsTrue(_sync.WaitingForAck);
            Assert.AreEqual(2, _session.SentSnapshots.Count, "Old ACK must not pump the new outbox.");
            _session.Acknowledge(newFrame);
            Assert.AreEqual(3, _session.SentSnapshots.Count);
            Assert.AreEqual(OpKinds.SnapshotChunk, _session.SentSnapshots[2].opKind);
            Assert.IsTrue(_sync.WaitingForAck, "The matching ACK should advance to waiting for the next frame.");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ReplayedSnapshotAckDoesNotAcknowledgeTheNextFrame(bool accepted)
        {
            Start(host: true);
            Begin();
            Invoke("PumpOutbox");
            var first = _session.SentSnapshots[0];
            _session.Acknowledge(first);
            var second = _session.SentSnapshots[1];
            Assert.AreNotEqual(first.opId, second.opId);

            _session.Acknowledge(first, accepted);

            Assert.IsTrue(_sync.WaitingForAck);
            Assert.AreEqual(2, _session.SentSnapshots.Count);
            Assert.AreEqual(0, _sync.SnapshotFailures);
            Assert.AreEqual(RecoveryPhase.Scene, _sync.RecoveryState);
            _session.Acknowledge(second);
            Assert.AreEqual(OpKinds.SnapshotEnd, _session.SentSnapshots[2].opKind);
        }

        [Test]
        public void DisconnectClearsLocalAckWaitDiscardsQueuedEditsAndRollsBackToConfirmedValue()
        {
            Start();
            var recovered = BecomeReady();
            QueueNameChange(recovered, "First unsent confirmation");
            var pending = _session.SentOperations[0];
            QueueNameChange(recovered, "Second queued change");
            Assert.IsTrue(_sync.WaitingForAck);
            Assert.AreEqual(1, _sync.Queued);
            Assert.AreEqual(1, _session.SentOperations.Count);

            _session.Disconnect();

            Assert.IsFalse(_sync.WaitingForAck);
            Assert.AreEqual(0, _sync.Queued);
            Assert.AreEqual(RecoveredName, recovered.name);
            AssertPaused();
            _session.Acknowledge(pending);
            _session.ChangeState(CollabConnectionState.Connected);
            Invoke("PumpOutbox");
            Assert.AreEqual(1, _session.SentOperations.Count, "Unconfirmed edits must not leak into the new connection.");
            Assert.IsFalse(_sync.WaitingForAck);
            AssertPaused();
        }

        [Test]
        public void OldLocalAckCannotConfirmTheNextOperationOrPreventItsDisconnectRollback()
        {
            Start();
            var recovered = BecomeReady();
            QueueNameChange(recovered, "Confirmed name");
            var first = _session.SentOperations[0];
            QueueNameChange(recovered, "Still unconfirmed");
            _session.Acknowledge(first);
            Assert.AreEqual(2, _session.SentOperations.Count);
            Assert.AreNotEqual(first.opId, _session.SentOperations[1].opId);

            _session.Acknowledge(first);

            Assert.IsTrue(_sync.WaitingForAck);
            Assert.AreEqual(2, _session.SentOperations.Count);
            _session.Disconnect();
            Assert.AreEqual("Confirmed name", recovered.name, "A replayed ACK must not confirm the newer local value.");
            Assert.IsFalse(_sync.WaitingForAck);
            AssertPaused();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AcceptedStaleRetryRestoresLatestQueuedLocalIntentAfterAPeerWrite(bool queueAnotherEdit)
        {
            Start();
            var recovered = BecomeReady();
            QueueNameChange(recovered, "First local value");
            var first = _session.SentOperations[0];
            if (queueAnotherEdit) QueueNameChange(recovered, "Latest queued value");

            PeerRename(first.oldValue, "Peer value");
            Assert.AreEqual("Peer value", recovered.name);
            _session.Acknowledge(first, false);
            Assert.AreEqual(2, _session.SentOperations.Count);
            var retry = _session.SentOperations[1];
            Assert.AreNotEqual(first.opId, retry.opId);
            Assert.AreEqual(first.newValue, retry.newValue);

            _session.Acknowledge(retry);

            var expected = queueAnotherEdit ? "Latest queued value" : "First local value";
            Assert.AreEqual(expected, recovered.name, "Accepted operations are not echoed to their sender.");
            var diff = new List<SceneOperation>();
            _sync.Diff.CollectInto(recovered, NodeId, diff);
            Assert.IsEmpty(diff, "The restored local intent must also be the diff baseline.");
            if (queueAnotherEdit)
            {
                Assert.IsTrue(_sync.WaitingForAck);
                Assert.AreEqual(3, _session.SentOperations.Count);
                Assert.AreEqual(expected, _session.SentOperations[2].newValue);
                _session.Acknowledge(_session.SentOperations[2]);
            }
            Assert.IsFalse(_sync.WaitingForAck);
            _session.Disconnect();
            Assert.AreEqual(expected, recovered.name, "Accepted intent must no longer be rolled back.");
        }

        [Test]
        public void AcceptedAckAfterTimeoutRestoresTheHostValueBeforeTheNextRecoverySnapshot()
        {
            Start(host: true);
            var recovered = new GameObject(RecoveredName);
            _sync.Registry.Adopt(recovered, NodeId);

            void DrainHostSnapshot()
            {
                var next = _session.SentSnapshots.Count;
                var acknowledged = 0;
                Invoke("PumpOutbox");
                while (_sync.WaitingForAck)
                {
                    Assert.Less(acknowledged++, 32, "This small scene must finish its snapshot.");
                    Assert.Less(next, _session.SentSnapshots.Count, "Every snapshot wait must have a sent frame.");
                    _session.Acknowledge(_session.SentSnapshots[next++]);
                }
                Assert.AreEqual(OpKinds.SnapshotEnd, _session.SentSnapshots[next - 1].opKind);
            }

            // Complete an actual host publication, including its empty terrain transfer and barrier.
            Begin();
            DrainHostSnapshot();
            _sync.Terrain.Tick();
            Invoke("TickRecovery", UnityEditor.EditorApplication.timeSinceStartup);
            Assert.IsNotNull(_session.SentControls.FindLast(message => message.type == MessageTypes.SyncEnd));
            End();
            Ready();
            Assert.IsTrue(_sync.CanEdit);

            const string acceptedName = "Accepted before recovery";
            QueueNameChange(recovered, acceptedName);
            var pending = _session.SentOperations[0];
            Assert.IsTrue(_sync.WaitingForAck);
            Invoke("TickRecovery", UnityEditor.EditorApplication.timeSinceStartup + 13.0);

            Assert.AreEqual(RecoveredName, recovered.name, "The timed-out local value is rolled back while its verdict is unknown.");
            Assert.AreEqual(RecoveryPhase.Requesting, _sync.RecoveryState);
            Assert.IsFalse(_sync.WaitingForAck);
            AssertPaused();

            _session.Acknowledge(pending); // The verdict is ordered before the next SyncBegin.

            Assert.AreEqual(acceptedName, recovered.name);
            Assert.AreEqual(RecoveryPhase.Requesting, _sync.RecoveryState);
            Assert.IsFalse(_sync.WaitingForAck);
            AssertPaused();
            var diff = new List<SceneOperation>();
            _sync.Diff.CollectInto(recovered, NodeId, diff);
            Assert.IsEmpty(diff, "The accepted late write must also update the local baseline.");

            Begin(NextEpoch);
            DrainHostSnapshot();
            var sawAcceptedName = false;
            var sawAcceptedNode = false;
            foreach (var frame in _session.SentSnapshots)
            {
                if (frame.syncId != NextEpoch || frame.opKind != OpKinds.SnapshotChunk) continue;
                foreach (var operation in JsonUtility.FromJson<SnapshotChunk>(frame.newValue).ops)
                {
                    if (operation.nodeId != NodeId) continue;
                    if (operation.opKind == OpKinds.AddNode)
                    {
                        Assert.AreEqual(acceptedName, JsonUtility.FromJson<SceneStructure.AddNodePayload>(operation.newValue).name);
                        sawAcceptedNode = true;
                    }
                    if (operation.propertyPath == ScenePropertyAccess.Name)
                    {
                        Assert.AreEqual(acceptedName, operation.newValue);
                        sawAcceptedName = true;
                    }
                }
            }
            Assert.IsTrue(sawAcceptedNode, "The new snapshot must reconstruct the accepted object name.");
            Assert.IsTrue(sawAcceptedName, "The new snapshot must publish the accepted name property.");
        }

        [Test]
        public void ANewerUncollectedUiEditSurvivesTheRetryAckAndRemainsDetectable()
        {
            Start();
            var recovered = BecomeReady();
            QueueNameChange(recovered, "Pending local value");
            var first = _session.SentOperations[0];
            PeerRename(first.oldValue, "Peer value");
            _session.Acknowledge(first, false);
            var retry = _session.SentOperations[1];
            recovered.name = "New UI value"; // The diff has not collected this edit yet.

            _session.Acknowledge(retry);

            Assert.AreEqual("New UI value", recovered.name);
            Assert.IsFalse(_sync.WaitingForAck);
            var diff = new List<SceneOperation>();
            _sync.Diff.CollectInto(recovered, NodeId, diff);
            Assert.AreEqual(1, diff.Count, "The ACK must not swallow a newer local edit into the baseline.");
            Assert.AreEqual(ScenePropertyAccess.Name, diff[0].PropertyPath);
            Assert.AreEqual("New UI value", diff[0].NewValue);
        }

        [Test]
        public void DisableRestoresEditingFlagsAndLateRecoveryFramesCannotReactivateIt()
        {
            Start();
            Begin();
            CompleteScene();
            End();

            _sync.Disable();
            Ready();
            Begin(NextEpoch);

            Assert.IsFalse(_sync.Enabled);
            Assert.IsFalse(_sync.CanEdit);
            Assert.AreEqual(RecoveryPhase.Off, _sync.RecoveryState);
            Assert.IsFalse(SceneSaveGuard.RecoveryBlocked);
            Assert.AreEqual(_sentinelFlags, _sentinel.hideFlags);
            Assert.AreEqual(_sentinelTransformFlags, _sentinel.transform.hideFlags);
        }

        sealed class RecoverySession : ISceneCollabSession, ICollabControlSession
        {
            public readonly List<WireMessage> SentControls = new List<WireMessage>();
            public readonly List<WireMessage> SentSnapshots = new List<WireMessage>();
            public readonly List<WireMessage> SentOperations = new List<WireMessage>();
            public CollabConnectionState State { get; private set; } = CollabConnectionState.Connected;
            public bool IsHost { get; set; }
            public bool IsScribe { get { return IsHost; } }
            public string ClientId { get { return "test-client"; } }
            public string RoomName { get { return "recovery-tests"; } }
            public string SceneGuid { get { return string.Empty; } }
            public string ScenePath { get { return string.Empty; } }
            public string SceneName { get { return string.Empty; } }
            public string[] LockedNodeIds { get { return Array.Empty<string>(); } }
            public string[] AssetGuids { get { return Array.Empty<string>(); } }
            public int AssetCount { get { return 0; } }
            public long Revision { get; private set; }
            public IReadOnlyList<ParticipantInfo> Participants { get { return Array.Empty<ParticipantInfo>(); } }

            public event Action StateChanged;
            public event Action<WireMessage> ControlReceived;
            public event Action<WireMessage> OperationReceived;
            public event Action<WireMessage> OperationAcknowledged;
            public event Action ParticipantsChanged { add { } remove { } }
            public event Action<string, string> ErrorReceived { add { } remove { } }
            public event Action LocksChanged { add { } remove { } }
            public event Action AssetManifestChanged { add { } remove { } }
            public event Action<WireMessage> ViewReceived { add { } remove { } }
            public event Action<WireMessage> TerrainReceived { add { } remove { } }

            public void DeliverControl(WireMessage message) { ControlReceived?.Invoke(message); }
            public void DeliverOperation(WireMessage message) { OperationReceived?.Invoke(message); }

            public void Acknowledge(WireMessage sent, bool accepted = true)
            {
                if (accepted) Revision++;
                OperationAcknowledged?.Invoke(new WireMessage
                {
                    type = MessageTypes.Ack, opId = sent.opId, syncId = sent.syncId,
                    accepted = accepted, code = accepted ? null : ErrorCodes.StaleRevision, revision = Revision,
                });
            }

            public void ChangeState(CollabConnectionState state)
            {
                State = state;
                StateChanged?.Invoke();
            }

            public void Connect(CollabSettings settings) { ChangeState(CollabConnectionState.Connected); }
            public void Disconnect() { ChangeState(CollabConnectionState.Disconnected); }
            public bool IsLockedByOther(string nodeId) { return false; }

            public bool SendControl(WireMessage message)
            {
                if (State != CollabConnectionState.Connected) return false;
                SentControls.Add(message);
                if (message.type == MessageTypes.Op) SentOperations.Add(message);
                return true;
            }

            public bool SendSnapshotFrame(WireMessage frame)
            {
                if (State != CollabConnectionState.Connected) return false;
                SentSnapshots.Add(frame);
                return true;
            }

            public bool SendOperation(SceneOperation operation)
            {
                throw new InvalidOperationException("Protocol-v2 operations must use the correlated control path.");
            }

            public bool SendLock(string nodeId, bool release) { return true; }
            public bool SendView(float px, float py, float pz, float rx, float ry, float rz, float rw, float fov) { return true; }
            public bool SendTerrain(WireMessage patch) { return State == CollabConnectionState.Connected; }
            public bool PublishAssetCount(int count) { return true; }
            public bool PublishAssets(string[] assetGuids) { return true; }
        }
    }
}
