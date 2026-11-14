using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// v0.5: the terrain path end to end, on a real TerrainData asset.
    ///
    /// <see cref="TerrainPatchTests"/> covers the wire format. This covers what the format cannot: that
    /// a sculpt is noticed at all (the scene diff sees nothing, because a terrain is a pointer to an
    /// asset), that the tile lands at the right offset on the other side, and that a terrain which does
    /// not match is refused instead of corrupted.
    /// </summary>
    public sealed class TerrainSyncTests
    {
        const string Folder = "Assets/__SceneCollabTerrainTest";
        const int Resolution = 129;

        GameObject _host;
        TerrainData _data;
        readonly List<TerrainSync> _controlled = new List<TerrainSync>();

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(Folder))
            {
                AssetDatabase.CreateFolder("Assets", "__SceneCollabTerrainTest");
            }

            _data = new TerrainData();
            _data.heightmapResolution = Resolution;
            _data.size = new Vector3(512f, 60f, 512f);

            _host = new GameObject("SceneCollab Terrain");
            var terrain = _host.AddComponent<Terrain>();
            terrain.terrainData = _data;
            AssetDatabase.CreateAsset(_data, Folder + "/Terrain.asset");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var sync in _controlled) sync.Unbind();
            _controlled.Clear();
            if (_host != null)
            {
                UnityEngine.Object.DestroyImmediate(_host);
            }

            AssetDatabase.DeleteAsset(Folder);
        }

        [Test]
        public void AHostSculptTravelsAsOneTileAtTheRightOffset()
        {
            var session = new FakeSession { IsHost = true };
            var sync = new TerrainSync();
            try
            {
                sync.Bind(session);

                // The first poll only takes the baseline: nothing has been sculpted yet.
                sync.Tick();
                Assert.AreEqual(0, session.SentTerrain.Count, "a fresh terrain must not send its whole heightmap");

                var blob = new float[8, 8];
                for (var z = 0; z < 8; z++)
                {
                    for (var x = 0; x < 8; x++)
                    {
                        blob[z, x] = 0.05f + 0.1f * (z * 8 + x) / 64f;
                    }
                }

                _data.SetHeights(20, 30, blob);
                EditorUtility.SetDirty(_data);
                TickAgain(sync);

                Assert.AreEqual(1, session.SentTerrain.Count, "one stroke must arrive as one tile");

                var tile = session.SentTerrain[0];
                Assert.AreEqual("height", tile.terrainKind);
                Assert.AreEqual(20, tile.terrainX, "tile x");
                Assert.AreEqual(30, tile.terrainZ, "tile z");
                Assert.AreEqual(8, tile.terrainWidth, "tile width");
                Assert.AreEqual(8, tile.terrainHeight, "tile height");
                Assert.AreEqual(_data.heightmapResolution, tile.terrainResolution, "tile resolution");

                float[,] region;
                Assert.IsTrue(TerrainPatchCodec.TryDecodeHeights(tile.terrainPayload, 8, 8, out region));
                Assert.AreEqual(0.05f, region[0, 0], 1f / 65535f, "first sample");
                Assert.AreEqual(0.05f + 0.1f * 63f / 64f, region[7, 7], 1f / 65535f, "last sample");

                Assert.AreEqual(1, sync.Sent, "the counter the window shows");
            }
            finally
            {
                sync.Unbind();
            }
        }

        [Test]
        public void AGuestAppliesThePatchToItsOwnTerrainAndMarksItDirty()
        {
            var session = new FakeSession { IsHost = false };
            var sync = new TerrainSync();
            try
            {
                sync.Bind(session);

                var payload = TerrainPatchCodec.EncodeHeights(Blob(4, 4, 0.5f), 0, 0, 4, 4);
                session.Deliver(new WireMessage
                {
                    type = MessageTypes.Terrain,
                    terrainValid = true,
                    terrainKind = "height",
                    terrainGuid = GuidOf(_data),
                    terrainResolution = _data.heightmapResolution,
                    terrainX = 10,
                    terrainZ = 12,
                    terrainWidth = 4,
                    terrainHeight = 4,
                    terrainPayload = payload,
                });

                Assert.AreEqual(1, sync.Applied, "the patch must be applied");
                Assert.AreEqual(0, sync.Skipped, "and must not be counted as skipped");

                var heights = _data.GetHeights(10, 12, 4, 4);
                Assert.AreEqual(0.5f, heights[0, 0], 1f / 65535f, "the tile origin was written");
                Assert.AreEqual(0.5f, heights[3, 3], 1f / 65535f, "the far corner was written");
                Assert.IsTrue(EditorUtility.IsDirty(_data), "the guest's asset must be marked dirty for the host to save");
            }
            finally
            {
                sync.Unbind();
            }
        }

        [Test]
        public void APatchForAnotherResolutionIsRefusedNotApplied()
        {
            var session = new FakeSession { IsHost = false };
            var sync = new TerrainSync();
            try
            {
                sync.Bind(session);

                session.Deliver(new WireMessage
                {
                    type = MessageTypes.Terrain,
                    terrainValid = true,
                    terrainKind = "height",
                    terrainGuid = GuidOf(_data),
                    terrainResolution = Resolution * 2,
                    terrainX = 0,
                    terrainZ = 0,
                    terrainWidth = 4,
                    terrainHeight = 4,
                    terrainPayload = TerrainPatchCodec.EncodeHeights(Blob(4, 4, 0.5f), 0, 0, 4, 4),
                });

                Assert.AreEqual(0, sync.Applied, "a mismatched terrain must not be written");
                Assert.AreEqual(1, sync.Skipped, "it must be reported instead");
                Assert.AreEqual(0f, _data.GetHeights(0, 0, 1, 1)[0, 0], 1e-6f, "and the terrain must be untouched");
            }
            finally
            {
                sync.Unbind();
            }
        }

        [Test]
        public void FirstPaintAfterBaselineIsSent()
        {
            var first = new TerrainLayer();
            var second = new TerrainLayer();
            var sync = new TerrainSync();
            try
            {
                _data.alphamapResolution = 16;
                _data.terrainLayers = new[] { first, second };
                var session = new FakeSession { IsHost = true };
                sync.Bind(session);
                sync.Tick();

                var paint = new float[1, 1, 2];
                paint[0, 0, 1] = 1f;
                _data.SetAlphamaps(3, 4, paint);
                EditorUtility.SetDirty(_data);
                Thread.Sleep((int)(TerrainSync.SplatPollSeconds * 1000) + 200);
                sync.Tick();

                var patch = session.SentTerrain.Find(p => p.terrainKind == "splat");
                Assert.IsNotNull(patch, "the first paint must not become the baseline");
                float[,,] decoded;
                Assert.IsTrue(TerrainPatchCodec.TryDecodeSplat(patch.terrainPayload, patch.terrainWidth,
                    patch.terrainHeight, 2, out decoded));
                Assert.AreEqual(1f, decoded[4 - patch.terrainZ, 3 - patch.terrainX, 1], 1f / 255f);

                var original = new float[1, 1, 2];
                original[0, 0, 0] = 1f;
                _data.SetAlphamaps(3, 4, original);
                patch.terrainValid = true;
                var guest = new FakeSession { IsHost = false };
                var guestSync = new TerrainSync();
                try
                {
                    guestSync.Bind(guest);
                    guest.Deliver(patch);
                    Assert.AreEqual(1, guestSync.Applied);
                    Assert.AreEqual(1f, _data.GetAlphamaps(3, 4, 1, 1)[0, 0, 1], 1f / 255f);
                }
                finally { guestSync.Unbind(); }
            }
            finally
            {
                sync.Unbind();
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void FullSyncRestartsForAJoiningPeerAndIncludesCurrentSamples()
        {
            var session = new FakeSession { IsHost = true };
            var sync = new TerrainSync();
            var first = new TerrainLayer();
            var second = new TerrainLayer();
            try
            {
                _data.heightmapResolution = 513;
                _data.alphamapResolution = 16;
                _data.terrainLayers = new[] { first, second };
                _data.SetHeights(100, 100, Blob(1, 1, 0.5f));
                var paint = new float[1, 1, 2];
                paint[0, 0, 1] = 1f;
                _data.SetAlphamaps(3, 4, paint);
                sync.Bind(session);
                sync.RequestFullSync();
                sync.Tick();
                Assert.Greater(session.SentTerrain.Count, 0);

                // A peer joining during a transfer must receive the beginning again.
                session.SentTerrain.Clear();
                sync.RequestFullSync();
                TickAgain(sync);

                var firstTile = session.SentTerrain.Find(p => p.terrainKind == "height"
                    && p.terrainX == 0 && p.terrainZ == 0);
                Assert.IsNotNull(firstTile);
                float[,] decoded;
                Assert.IsTrue(TerrainPatchCodec.TryDecodeHeights(firstTile.terrainPayload,
                    firstTile.terrainWidth, firstTile.terrainHeight, out decoded));
                Assert.AreEqual(0.5f, decoded[100, 100], 1f / 65535f);
                for (var i = 0; i < 3; i++) TickAgain(sync);
                Assert.IsNotNull(session.SentTerrain.Find(p => p.terrainKind == "height"
                    && p.terrainX == 512 && p.terrainZ == 512), "edge tiles must be included");
                var splat = session.SentTerrain.Find(p => p.terrainKind == "splat");
                Assert.IsNotNull(splat, "the transfer must include painted layers");
                float[,,] decodedSplat;
                Assert.IsTrue(TerrainPatchCodec.TryDecodeSplat(splat.terrainPayload,
                    splat.terrainWidth, splat.terrainHeight, splat.terrainLayers, out decodedSplat));
                Assert.AreEqual(1f, decodedSplat[4, 3, 1], 1f / 255f);
            }
            finally
            {
                sync.Unbind();
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void GuestStrokeIsRestoredUntilItsCorrelatedGrantThenCommittedOnlyOnAck()
        {
            var sync = StartControl(out var session);
            _data.SetHeights(20, 30, Blob(8, 8, 0.5f));
            Assert.AreEqual(0f, Sample(20, 30), 1e-6f, "unleased callbacks must restore immediately");
            Assert.AreEqual(0, session.SentTerrain.Count);
            var request = LockRequest(session);
            Assert.IsTrue(string.IsNullOrEmpty(request.terrainLeaseId), "new leases are server-assigned");
            TerrainRegionRules.TryMapPatch(Resolution, 20, 30, 8, 8, out var x, out var z, out var w, out var h);
            Assert.AreEqual(new RectInt(x, z, w, h), new RectInt(request.regionX, request.regionZ, request.regionWidth, request.regionHeight));
            session.DeliverControl(new WireMessage { type = MessageTypes.TerrainLocks, accepted = true,
                opId = "unrelated", terrainLocks = Array.Empty<TerrainRegionLease>() });
            Assert.AreEqual(0, session.SentTerrain.Count, "a different request cannot apply the buffered stroke");
            var lease = Grant(session, request);
            Assert.AreEqual(0.5f, Sample(20, 30), 2f / 65535f);
            Assert.AreEqual(1, session.SentTerrain.Count);
            var patch = session.SentTerrain[0];
            Assert.AreEqual(lease.leaseId, patch.terrainLeaseId);
            Assert.IsFalse(string.IsNullOrEmpty(patch.opId));
            Ack(session, patch, true);
            sync.EditingAllowed = false;
            Assert.AreEqual(0.5f, Sample(20, 30), 2f / 65535f, "accepted terrain survives pause");
        }

        [Test]
        public void AdditionalStrokesWhileLockPendingAreDiscardedAndDenialRestoresAll()
        {
            var sync = StartControl(out var session);
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            _data.SetHeights(100, 100, Blob(1, 1, 0.8f));
            Assert.AreEqual(0f, Sample(20, 30), 1e-6f);
            Assert.AreEqual(0f, Sample(100, 100), 1e-6f);
            Assert.AreEqual(1, session.SentControl.Count);
            session.DeliverControl(new WireMessage { type = MessageTypes.TerrainLocks, accepted = false,
                opId = LockRequest(session).opId, code = ErrorCodes.Locked, terrainLocks = Array.Empty<TerrainRegionLease>() });
            Assert.AreEqual(0, session.SentTerrain.Count);
            StringAssert.Contains("denied", sync.LastNote);
            Assert.IsTrue(sync.EditingAllowed, "an ordinary lock denial needs a retry, not whole-scene recovery");
        }

        [Test]
        public void IntersectingRemotePatchInvalidatesBufferedStrokeEvenIfLockLaterSucceeds()
        {
            var sync = StartControl(out var session);
            _data.SetHeights(20, 30, Blob(2, 2, 0.5f));
            session.Deliver(Remote(20, 30, 0.8f));
            Grant(session, LockRequest(session));
            Assert.AreEqual(0.8f, Sample(20, 30), 2f / 65535f);
            Assert.AreEqual(0f, Sample(21, 31), 1e-6f);
            Assert.AreEqual(0, session.SentTerrain.Count);
            Assert.IsNotNull(session.SentControl.Find(m => m.type == MessageTypes.TerrainUnlock));
            StringAssert.Contains("retry", sync.LastNote);
        }

        [Test]
        public void DisjointRemotePatchPreservesPendingStrokeAndDoesNotEcho()
        {
            StartControl(out var session);
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            session.Deliver(Remote(110, 110, 0.8f));
            Grant(session, LockRequest(session));
            Assert.AreEqual(1, session.SentTerrain.Count);
            Ack(session, session.SentTerrain[0], true);
            Assert.AreEqual(0.8f, Sample(110, 110), 2f / 65535f);
            Assert.AreEqual(0.5f, Sample(20, 30), 2f / 65535f);
        }

        [Test]
        public void PausedNativeEditsAreImmediatelyRestoredWithoutRequestingLocks()
        {
            var sync = StartControl(out var session, allow: false);
            Assert.IsFalse(sync.EditingAllowed);
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            Assert.AreEqual(0f, Sample(20, 30), 1e-6f);
            Assert.AreEqual(0, session.SentControl.Count);
            Assert.AreEqual(0, session.SentTerrain.Count);
        }

        [Test]
        public void PauseDiscardsPendingGrantAndReleasesALateLease()
        {
            var sync = StartControl(out var session);
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            var request = LockRequest(session);
            sync.EditingAllowed = false;
            var lease = Grant(session, request);
            Assert.AreEqual(0f, Sample(20, 30), 1e-6f);
            Assert.AreEqual(0, session.SentTerrain.Count);
            Assert.IsNotNull(session.SentControl.Find(m => m.type == MessageTypes.TerrainUnlock && m.terrainLeaseId == lease.leaseId));
        }

        [Test]
        public void RejectionRestoresLastAcknowledgedBaselineAndRaisesFailure()
        {
            var sync = StartControl(out var session);
            string failure = null;
            sync.Failed += reason => failure = reason;
            _data.SetHeights(20, 30, Blob(1, 1, 0.25f));
            Grant(session, LockRequest(session));
            Ack(session, session.SentTerrain[0], true);
            _data.SetHeights(20, 30, Blob(1, 1, 0.75f));
            Assert.AreEqual(2, session.SentTerrain.Count, "an existing own lease permits the next stroke");
            Ack(session, session.SentTerrain[1], false);
            Assert.AreEqual(0.25f, Sample(20, 30), 2f / 65535f);
            StringAssert.Contains("rejected", failure);
            Assert.IsFalse(sync.EditingAllowed);
        }

        [Test]
        public void PauseRollsBackUnacknowledgedPatchButPreservesItsAcceptedVerdict()
        {
            var sync = StartControl(out var session);
            var failures = 0;
            sync.Failed += reason => failures++;
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            Grant(session, LockRequest(session));
            var patch = session.SentTerrain[0];
            sync.EditingAllowed = false;
            Assert.AreEqual(0f, Sample(20, 30), 1e-6f);
            Ack(session, patch, true);
            Assert.AreEqual(0, failures, "an ordered ACK during the retry pause is not a protocol failure");
            Assert.AreEqual(0.5f, Sample(20, 30), 2f / 65535f, "accepted samples become authoritative even while paused");
            Assert.AreEqual(1, session.SentTerrain.Count, "committing the verdict must not echo or resend the edit");
            sync.EditingAllowed = true;
            Assert.IsFalse(sync.EditingAllowed, "the parent must finish recovery before resuming");
        }

        [TestCase(true)]
        [TestCase(false)]
        public void HostRetrySnapshotHonorsTheVerdictOfATileCanceledBeforeTheRecoveryBarrier(bool accepted)
        {
            var sync = StartControl(out var session, host: true);
            var failures = 0;
            sync.Failed += reason => failures++;
            // Four captured tiles, of which only the first has actually been sent.
            _data.SetHeights(0, 0, Blob(Resolution, Resolution, 0.5f));
            Grant(session, LockRequest(session));
            Assert.AreEqual(1, session.SentTerrain.Count);
            var live = session.SentTerrain[0];

            sync.EditingAllowed = false; // Parent RetryRecovery pauses before the server ACK.
            Assert.AreEqual(0f, Sample(20, 30), 1e-6f);
            Ack(session, live, accepted);
            var expected = accepted ? 0.5f : 0f;
            Assert.AreEqual(expected, Sample(20, 30), 2f / 65535f);
            Assert.AreEqual(0f, Sample(Resolution - 1, Resolution - 1), 1e-6f, "unsent tiles remain discarded");
            Assert.AreEqual(0, failures);
            Assert.IsFalse(sync.EditingAllowed);

            sync.BeginRecovery("retry-after-verdict");
            sync.RequestFullSync("retry-after-verdict");
            sync.Tick();
            var snapshot = session.SentTerrain.Find(p => p.snapshot && p.terrainX == 0 && p.terrainZ == 0);
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(TerrainPatchCodec.TryDecodeHeights(snapshot.terrainPayload,
                snapshot.terrainWidth, snapshot.terrainHeight, out var samples));
            Assert.AreEqual(expected, samples[30, 20], 2f / 65535f,
                "the next host snapshot must retain the accepted tile, not the pre-ACK rollback baseline");
            Assert.AreEqual(4, sync.FullSyncSent);
            Assert.AreEqual(5, session.SentTerrain.Count, "there is no resumed live-stroke outbox");
        }

        [Test]
        public void CanceledLiveVerdictBlocksSnapshotPublicationUntilRejectionArrives()
        {
            var sync = StartControl(out var session, host: true);
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            Grant(session, LockRequest(session));
            var live = session.SentTerrain[0];
            sync.EditingAllowed = false;
            sync.BeginRecovery("barrier-before-rejection");
            sync.RequestFullSync("barrier-before-rejection");
            sync.Tick();
            Assert.IsTrue(sync.FullSyncInProgress, "the queued recovery must wait for the unknown verdict");
            Assert.AreEqual(0, sync.FullSyncSent);
            Assert.AreEqual(1, session.SentTerrain.Count);

            // Another peer's SyncRequest may precede our patch, so a rejection after SyncBegin is valid.
            Ack(session, live, false);
            sync.Tick();
            Assert.AreEqual(4, sync.FullSyncSent);
            Assert.AreEqual(0, sync.SkippedDuringRecovery);
            Assert.AreEqual(0f, Sample(20, 30), 1e-6f);
        }

        [TestCase("active")]
        [TestCase("canceled")]
        [TestCase("ended")]
        public void AcceptedCanceledPatchCannotOverwriteALaterRecoveryGeneration(string phase)
        {
            var sync = StartControl(out var session, host: true);
            var failures = 0;
            sync.Failed += reason => failures++;
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            Grant(session, LockRequest(session));
            var live = session.SentTerrain[0];
            sync.EditingAllowed = false;
            sync.BeginRecovery("new-generation");
            sync.RequestFullSync("new-generation");
            sync.Tick();
            var authoritative = Remote(20, 30, 0.8f);
            authoritative.snapshot = true;
            authoritative.syncId = "new-generation";
            session.Deliver(authoritative);
            if (phase == "canceled") sync.CancelRecovery();
            if (phase == "ended") sync.EndRecovery();

            Ack(session, live, true);
            Assert.AreEqual(1, failures, "acceptance after a newer barrier violates the ordered protocol");
            Assert.AreEqual(0.8f, Sample(20, 30), 2f / 65535f, "a stale accepted tile must not overwrite newer samples");
            Assert.AreEqual(1, sync.SkippedDuringRecovery);
            Assert.IsFalse(sync.FullSyncInProgress);
            Assert.IsFalse(sync.EditingAllowed);
            sync.Tick();
            Assert.AreEqual(1, session.SentTerrain.Count, "failure cancels the queued snapshot before any tile is published");
        }

        [Test]
        public void UnknownCanceledVerdictTimesOutWithoutPublishingAnOlderSnapshot()
        {
            double now = 0;
            var sync = StartControl(out var session, host: true, clock: () => now);
            var failures = 0;
            sync.Failed += reason => failures++;
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            Grant(session, LockRequest(session));
            sync.EditingAllowed = false;
            sync.BeginRecovery("missing-verdict");
            sync.RequestFullSync("missing-verdict");
            now = TerrainSync.AckTimeoutSeconds + 0.1;
            sync.Tick();
            Assert.AreEqual(1, failures);
            Assert.AreEqual(0, sync.FullSyncSent);
            Assert.IsFalse(sync.FullSyncInProgress);
            Assert.IsFalse(sync.EditingAllowed);
            Assert.AreEqual(1, session.SentTerrain.Count);
        }

        [Test]
        public void ExpiredLeaseDoesNotDiscardOutstandingAckAndIdleReleaseWaitsForIt()
        {
            double now = 0;
            long utc = 100000;
            var sync = StartControl(out var session, clock: () => now, utc: () => utc);
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            var lease = Grant(session, LockRequest(session), utc + 1000);
            now = 3; utc += 2000;
            session.DeliverControl(new WireMessage { type = MessageTypes.TerrainLocks, terrainLocks = Array.Empty<TerrainRegionLease>() });
            sync.Tick();
            Assert.IsNull(session.SentControl.Find(m => m.type == MessageTypes.TerrainUnlock));
            Ack(session, session.SentTerrain[0], true);
            Assert.AreEqual(0.5f, Sample(20, 30), 2f / 65535f);
            now += TerrainSync.IdleLeaseSeconds + 0.1;
            sync.Tick();
            Assert.IsNotNull(session.SentControl.Find(m => m.type == MessageTypes.TerrainUnlock && m.terrainLeaseId == lease.leaseId));
        }

        [Test]
        public void HostIngestsGuestPatchWithoutCallbackEcho()
        {
            var sync = StartControl(out var session, host: true);
            var patch = Remote(10, 12, 0.5f);
            patch.clientId = "guest";
            session.Deliver(patch);
            sync.Tick();
            Assert.AreEqual(1, sync.Applied);
            Assert.AreEqual(0.5f, Sample(10, 12), 2f / 65535f);
            Assert.AreEqual(0, session.SentTerrain.Count);
            Assert.AreEqual(0, session.SentControl.Count);
        }

        [Test]
        public void OtherOwnersRegionIsRevertedWithoutAcquiringOrSending()
        {
            StartControl(out var session);
            session.DeliverControl(new WireMessage { type = MessageTypes.TerrainLocks, terrainLocks = new[] {
                new TerrainRegionLease { terrainGuid = GuidOf(_data), clientId = "other", displayName = "Other",
                    leaseId = "occupied", x = 0, z = 0, width = 16, height = 16,
                    expiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 15000 } } });
            _data.SetHeights(20, 30, Blob(1, 1, 0.5f));
            Assert.AreEqual(0f, Sample(20, 30), 1e-6f);
            Assert.AreEqual(0, session.SentTerrain.Count);
            Assert.AreEqual(0, session.SentControl.Count);
        }

        [Test]
        public void FullSyncIsQueuedBeforeTickBoundedToEightAndWaitsForAllAcks()
        {
            _data.heightmapResolution = 513;
            var sync = StartControl(out var session, host: true);
            sync.BeginRecovery("recovery");
            sync.RequestFullSync("recovery");
            Assert.IsTrue(sync.FullSyncInProgress, "the parent checks this before terrain's first Tick");
            Assert.AreEqual(25, sync.FullSyncTotal);
            var acknowledged = 0;
            for (var tick = 0; tick < 10 && sync.FullSyncInProgress; tick++)
            {
                var before = session.SentTerrain.Count;
                sync.Tick();
                Assert.LessOrEqual(session.SentTerrain.Count - before, 8);
                if (session.SentTerrain.Count > acknowledged) Assert.IsTrue(sync.FullSyncInProgress);
                while (acknowledged < session.SentTerrain.Count)
                {
                    var patch = session.SentTerrain[acknowledged++];
                    Assert.IsTrue(patch.snapshot);
                    Assert.IsTrue(patch.terrainValid);
                    Assert.AreEqual("recovery", patch.syncId);
                    Assert.IsTrue(string.IsNullOrEmpty(patch.terrainLeaseId));
                    Ack(session, patch, true);
                }
            }
            Assert.IsFalse(sync.FullSyncInProgress);
            Assert.AreEqual(25, sync.FullSyncSent);
            Assert.AreEqual(sync.FullSyncTotal, sync.FullSyncSent);
            Assert.IsFalse(sync.EditingAllowed);
            sync.EndRecovery();
            Assert.IsFalse(sync.EditingAllowed, "SyncReady is owned by the parent");
            sync.EditingAllowed = true;
            Assert.IsTrue(sync.EditingAllowed);
        }

        [Test]
        public void RecoveryCountsOnlyAppliedCurrentGenerationTilesAndReportsInvalidOnes()
        {
            var sync = StartControl(out var session);
            sync.BeginRecovery("current");
            var patch = Remote(10, 12, 0.5f);
            patch.snapshot = true;
            patch.syncId = "old";
            session.Deliver(patch);
            Assert.AreEqual(0, sync.RecoveryApplied);
            patch.syncId = "current";
            session.Deliver(patch);
            session.Deliver(patch);
            Assert.AreEqual(1, sync.RecoveryApplied, "duplicate tiles do not hide missing ones");
            sync.BeginRecovery("current");
            Assert.AreEqual(1, sync.RecoveryApplied, "duplicate begin must not reset counters");
            _data.SetHeights(10, 12, Blob(1, 1, 0.9f));
            Assert.AreEqual(0.5f, Sample(10, 12), 2f / 65535f, "snapshot became the paused rollback baseline");
            patch.terrainPayload = "invalid";
            session.Deliver(patch);
            Assert.AreEqual(1, sync.SkippedDuringRecovery);
            Assert.AreEqual(1, sync.RecoveryApplied);
            Assert.AreEqual(0, session.SentTerrain.Count);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedOrCanceledRecoveryStopsAllQueuedAndOutstandingTerrain(bool cancel)
        {
            _data.heightmapResolution = 513;
            var sync = StartControl(out var session, host: true);
            sync.RequestFullSync("failed");
            sync.Tick();
            Assert.AreEqual(8, session.SentTerrain.Count);
            if (cancel) sync.CancelRecovery();
            else session.DeliverControl(new WireMessage { type = MessageTypes.SyncReady, syncId = "failed", accepted = false });
            Assert.IsFalse(sync.FullSyncInProgress);
            foreach (var patch in session.SentTerrain.ToArray()) Ack(session, patch, true);
            sync.Tick();
            Assert.AreEqual(8, session.SentTerrain.Count);
            Assert.IsFalse(sync.EditingAllowed);
        }

        [Test]
        public void SnapshotRejectionRaisesFailureAndDoesNotRetrySending()
        {
            var sync = StartControl(out var session, host: true);
            var failures = 0;
            sync.Failed += reason => failures++;
            sync.RequestFullSync("rejected");
            sync.Tick();
            Ack(session, session.SentTerrain[0], false);
            var sent = session.SentTerrain.Count;
            sync.Tick();
            Assert.AreEqual(sent, session.SentTerrain.Count);
            Assert.AreEqual(1, failures);
            Assert.AreEqual(1, sync.SkippedDuringRecovery);
            Assert.IsFalse(sync.FullSyncInProgress);
        }

        TerrainSync StartControl(out ControlSession session, bool host = false, bool allow = true,
            Func<double> clock = null, Func<long> utc = null)
        {
            session = new ControlSession { IsHost = host };
            var sync = new TerrainSync(clock, utc);
            _controlled.Add(sync);
            sync.Bind(session);
            Assert.IsFalse(sync.EditingAllowed, "control sessions must start paused");
            sync.EditingAllowed = allow;
            return sync;
        }

        float Sample(int x, int z) => _data.GetHeights(x, z, 1, 1)[0, 0];

        WireMessage Remote(int x, int z, float value)
        {
            return new WireMessage { type = MessageTypes.Terrain, terrainValid = true, opId = Guid.NewGuid().ToString("N"),
                terrainGuid = GuidOf(_data), terrainKind = TerrainPatchCodec.KindHeight, terrainResolution = _data.heightmapResolution,
                terrainX = x, terrainZ = z, terrainWidth = 1, terrainHeight = 1,
                terrainPayload = TerrainPatchCodec.EncodeHeights(Blob(1, 1, value), 0, 0, 1, 1) };
        }

        static WireMessage LockRequest(ControlSession session)
        {
            var request = session.SentControl.Find(m => m.type == MessageTypes.TerrainLock);
            Assert.IsNotNull(request, "the native callback must request a lease");
            return request;
        }

        static TerrainRegionLease Grant(ControlSession session, WireMessage request, long expiresAt = 0)
        {
            var lease = new TerrainRegionLease { terrainGuid = request.terrainGuid, leaseId = Guid.NewGuid().ToString("N"),
                clientId = session.ClientId, displayName = "Test owner", x = request.regionX, z = request.regionZ,
                width = request.regionWidth, height = request.regionHeight,
                expiresAt = expiresAt == 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 15000 : expiresAt };
            session.DeliverControl(new WireMessage { type = MessageTypes.TerrainLocks, opId = request.opId,
                accepted = true, terrainLeaseId = lease.leaseId, terrainLocks = new[] { lease } });
            return lease;
        }

        static void Ack(ControlSession session, WireMessage patch, bool accepted)
        {
            session.DeliverControl(new WireMessage { type = MessageTypes.TerrainAck, opId = patch.opId,
                accepted = accepted, syncId = patch.syncId, code = accepted ? null : "terrain_lease_invalid" });
        }

        /// <summary>Advance the dirty-version fallback used by legacy CPU painting APIs.</summary>
        static void TickAgain(TerrainSync sync)
        {
            Thread.Sleep((int)(TerrainSync.PollSeconds * 1000) + 200);
            sync.Tick();
        }

        static float[,] Blob(int width, int height, float value)
        {
            var heights = new float[height, width];
            for (var z = 0; z < height; z++)
            {
                for (var x = 0; x < width; x++)
                {
                    heights[z, x] = value;
                }
            }

            return heights;
        }

        static string GuidOf(UnityEngine.Object asset)
        {
            string guid;
            long localId;
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out guid, out localId));
            return guid;
        }

        /// <summary>
        /// The four members <see cref="TerrainSync"/> touches, and nothing else. A real session needs a
        /// server; the point of these tests is that the terrain path does not.
        /// </summary>
        class FakeSession : ISceneCollabSession
        {
            public bool IsHost { get; set; }

            public readonly List<WireMessage> SentTerrain = new List<WireMessage>();

            public void Deliver(WireMessage patch)
            {
                var handler = TerrainReceived;
                if (handler != null)
                {
                    handler(patch);
                }
            }

            public bool SendTerrain(WireMessage patch)
            {
                SentTerrain.Add(patch);
                return true;
            }

            public event Action<WireMessage> TerrainReceived;

            public CollabConnectionState State
            {
                get { return CollabConnectionState.Connected; }
            }

            public string ClientId
            {
                get { return "fake"; }
            }

            public bool IsScribe
            {
                get { return false; }
            }

            public string SceneGuid
            {
                get { return string.Empty; }
            }

            public string ScenePath
            {
                get { return string.Empty; }
            }

            public string SceneName
            {
                get { return string.Empty; }
            }

            public string[] LockedNodeIds
            {
                get { return Array.Empty<string>(); }
            }

            public string[] AssetGuids
            {
                get { return Array.Empty<string>(); }
            }

            public int AssetCount
            {
                get { return 0; }
            }

            public bool IsLockedByOther(string nodeId)
            {
                return false;
            }

            public long Revision
            {
                get { return 0; }
            }

            public string RoomName
            {
                get { return string.Empty; }
            }

            public IReadOnlyList<ParticipantInfo> Participants
            {
                get { return Array.Empty<ParticipantInfo>(); }
            }

            public event Action StateChanged;

            public event Action ParticipantsChanged;

            public event Action<WireMessage> OperationReceived;

            public event Action<WireMessage> OperationAcknowledged;

            public event Action<string, string> ErrorReceived;

            public event Action LocksChanged;

            public event Action AssetManifestChanged;

            public event Action<WireMessage> ViewReceived;

            public void Connect(CollabSettings settings)
            {
                throw new NotSupportedException();
            }

            public void Disconnect()
            {
                throw new NotSupportedException();
            }

            public bool SendOperation(SceneCollab.Domain.SceneOperation operation)
            {
                throw new NotSupportedException();
            }

            public bool SendLock(string nodeId, bool release)
            {
                throw new NotSupportedException();
            }

            public bool SendSnapshotFrame(WireMessage frame)
            {
                throw new NotSupportedException();
            }

            public bool SendView(float posX, float posY, float posZ, float rotX, float rotY, float rotZ, float rotW,
                float fov)
            {
                throw new NotSupportedException();
            }

            public bool PublishAssetCount(int count)
            {
                throw new NotSupportedException();
            }

            public bool PublishAssets(string[] assetGuids)
            {
                throw new NotSupportedException();
            }
        }

        sealed class ControlSession : FakeSession, ICollabControlSession
        {
            public readonly List<WireMessage> SentControl = new List<WireMessage>();
            public event Action<WireMessage> ControlReceived;
            public bool SendControl(WireMessage message) { SentControl.Add(message); return true; }
            public void DeliverControl(WireMessage message) { ControlReceived?.Invoke(message); }
        }
    }
}
