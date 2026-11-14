using SceneCollab.Contracts.Protocol;
using static SceneCollab.Domain.Tests.RoomSocketFixture;

namespace SceneCollab.Domain.Tests;

public sealed class RecoveryBarrierTests
{
    [Fact]
    public async Task FirstHostGetsWelcomePresenceThenBeginAndMustCompleteTheFullBarrier()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        Assert.Equal(new[] { MessageTypes.Welcome, MessageTypes.Presence, MessageTypes.SyncBegin },
            host.Socket.Messages.Take(3).Select(m => m.type));
        var begin = host.Socket.Messages.Single(m => m.type == MessageTypes.SyncBegin);
        Assert.Equal(host.Id, begin.clientId);
        Assert.NotEmpty(begin.syncId);
        Assert.Equal(ErrorCodes.SyncInProgress, (await test.OpAsync(host)).code);
        Assert.Equal(ErrorCodes.SyncInProgress, (await LockAsync(host)).code);
        host.Send(new WireMessage { type = MessageTypes.Lock, lockNodeId = "node", opId = "node-lock" });
        Assert.Equal(ErrorCodes.SyncInProgress,
            (await host.WaitAsync(m => m.type == MessageTypes.Error && m.opId == "node-lock")).code);
        var live = Patch();
        host.Send(live);
        Assert.Equal(ErrorCodes.SyncInProgress,
            (await host.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == live.opId)).code);
        await test.CompleteAsync(host, host.Epoch, host);
        Assert.True((await test.OpAsync(host)).accepted);
        Assert.True((await LockAsync(host)).accepted);
    }

    [Fact]
    public async Task EndRequiresCurrentHostSceneEndAndExactAcceptedTerrainCountThenEveryPeerReady()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        var epoch = guest.Epoch;
        var bad = Patch(epoch: epoch);
        bad.terrainPayload = "AAAA";
        host.Send(bad);
        Assert.False((await host.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == bad.opId)).accepted);
        var valid = Patch(epoch: epoch);
        host.Send(valid);
        Assert.True((await host.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == valid.opId)).accepted);
        var relayed = await guest.WaitAsync(m => m.type == MessageTypes.Terrain && m.opId == valid.opId);
        Assert.Equal(epoch, relayed.syncId);
        Assert.True(relayed.snapshot);

        host.Send(new WireMessage { type = MessageTypes.SyncEnd, syncId = epoch, syncTerrainTiles = 1, opId = "early-end" });
        Assert.Equal(ErrorCodes.SyncIncomplete,
            (await host.WaitAsync(m => m.type == MessageTypes.Error && m.opId == "early-end")).code);
        Ready(guest, epoch);
        Assert.Equal(ErrorCodes.SyncIncomplete,
            (await guest.WaitAsync(m => m.type == MessageTypes.SyncReady && !m.accepted)).code);

        Assert.True((await test.OpAsync(host, OpKinds.SnapshotEnd, epoch)).accepted);
        var sceneEnd = await guest.WaitAsync(m => m.type == MessageTypes.Op && m.opKind == OpKinds.SnapshotEnd);
        Assert.Equal(epoch, sceneEnd.syncId);
        host.Send(new WireMessage { type = MessageTypes.SyncEnd, syncId = epoch, syncTerrainTiles = 0, opId = "count-mismatch" });
        Assert.Equal(ErrorCodes.SyncIncomplete,
            (await host.WaitAsync(m => m.type == MessageTypes.Error && m.opId == "count-mismatch")).code);

        guest.Send(new WireMessage { type = MessageTypes.SyncEnd, syncId = epoch, syncTerrainTiles = 1,
            clientId = host.Id, opId = "forged-end" });
        var forged = await guest.WaitAsync(m => m.type == MessageTypes.Error && m.opId == "forged-end");
        Assert.Equal(ErrorCodes.NotHost, forged.code);
        Assert.Equal(guest.Id, forged.clientId);

        host.Send(new WireMessage { type = MessageTypes.SyncEnd, syncId = epoch, syncTerrainTiles = 1, clientId = guest.Id });
        var end = await guest.WaitAsync(m => m.type == MessageTypes.SyncEnd && m.syncId == epoch);
        Assert.Equal(host.Id, end.clientId);
        Assert.Equal(1, end.syncTerrainTiles);
        var late = Patch(epoch: epoch);
        host.Send(late);
        Assert.Equal(ErrorCodes.SyncIncomplete,
            (await host.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == late.opId)).code);
        Assert.Equal(ErrorCodes.SyncIncomplete, (await test.OpAsync(host, OpKinds.SnapshotChunk, epoch)).code);

        // A guest claiming to be the host contributes only its own authenticated vote.
        guest.Send(new WireMessage { type = MessageTypes.SyncReady, syncId = epoch, accepted = true, clientId = host.Id });
        Assert.Equal(ErrorCodes.SyncInProgress, (await test.OpAsync(guest)).code);
        Assert.DoesNotContain(host.Socket.Messages, m => m.type == MessageTypes.SyncReady && m.accepted);
        Ready(host, epoch);
        await guest.WaitAsync(m => m.type == MessageTypes.SyncReady && m.accepted && m.syncId == epoch);
        Assert.True((await test.OpAsync(guest)).accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuestCannotPretendToSupplySnapshotsInsideOrOutsideRecovery(bool complete)
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        if (complete) await test.CompleteAsync(host, guest.Epoch, host, guest);

        foreach (var kind in new[] { OpKinds.SetProperty, OpKinds.SnapshotStart, OpKinds.SnapshotEnd })
        {
            var request = new WireMessage { type = MessageTypes.Op, opId = Guid.NewGuid().ToString("N"),
                opKind = kind, snapshot = kind == OpKinds.SetProperty, syncId = guest.Epoch,
                clientId = host.Id, revision = test.Room.State.Revision };
            guest.Send(request);
            var ack = await guest.WaitAsync(m => m.type == MessageTypes.Ack && m.opId == request.opId);
            Assert.False(ack.accepted);
            Assert.Equal(ErrorCodes.NotHost, ack.code);
        }
        var patch = Patch(epoch: guest.Epoch);
        patch.clientId = host.Id;
        guest.Send(patch);
        Assert.Equal(ErrorCodes.NotHost,
            (await guest.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == patch.opId)).code);
    }

    [Fact]
    public async Task RestartClearsAllLeasesAndStaleEpochErrorsAreCorrelatedWithoutUnlockingOrPoisoningTheNewEpoch()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        var oldEpoch = guest.Epoch;
        await test.CompleteAsync(host, oldEpoch, host, guest);
        Assert.True((await LockAsync(host)).accepted);
        Assert.True((await LockAsync(guest, 8, 8)).accepted);
        guest.Send(new WireMessage { type = MessageTypes.SyncRequest });
        var next = await guest.WaitAsync(m => m.type == MessageTypes.SyncBegin && m.syncId != oldEpoch);
        Assert.Empty(test.Room.State.TerrainLeases.Snapshot());
        Assert.Empty((await guest.WaitAsync(m => m.type == MessageTypes.TerrainLocks)).terrainLocks);

        host.Send(new WireMessage { type = MessageTypes.SyncEnd, syncId = oldEpoch, opId = "stale-end" });
        var rejected = await host.WaitAsync(m => m.type == MessageTypes.Error && m.opId == "stale-end");
        Assert.Equal(oldEpoch, rejected.syncId);
        Assert.Equal(ErrorCodes.SyncMismatch, rejected.code);
        Ready(guest, oldEpoch, false);
        var staleFailure = await guest.WaitAsync(m => m.type == MessageTypes.SyncReady && m.syncId == oldEpoch && !m.accepted);
        Assert.Equal(ErrorCodes.SyncMismatch, staleFailure.code);
        Assert.Equal(ErrorCodes.SyncInProgress, (await test.OpAsync(host)).code);
        Assert.Equal(ErrorCodes.SyncMismatch, (await test.OpAsync(host, OpKinds.SnapshotEnd, oldEpoch)).code);
        await test.EndAsync(host, next.syncId);
        Ready(host, next.syncId);
        Ready(guest, oldEpoch);
        Assert.Equal(ErrorCodes.SyncMismatch,
            (await guest.WaitAsync(m => m.type == MessageTypes.SyncReady && m.syncId == oldEpoch && !m.accepted)).code);
        Assert.Equal(ErrorCodes.SyncInProgress, (await test.OpAsync(host)).code);
        Ready(guest, next.syncId);
        await guest.WaitAsync(m => m.type == MessageTypes.SyncReady && m.syncId == next.syncId && m.accepted);
        Assert.True((await test.OpAsync(guest)).accepted);
    }

    [Fact]
    public async Task EarlyPeerFailureImmediatelyNotifiesHostAndRequiresANewEpoch()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        Ready(guest, guest.Epoch, false);
        var failed = await host.WaitAsync(m => m.type == MessageTypes.SyncReady && !m.accepted);
        Assert.Equal(guest.Id, failed.clientId);
        Assert.Equal(ErrorCodes.SyncFailed, failed.code);
        Assert.Equal(ErrorCodes.SyncFailed, (await test.OpAsync(host, OpKinds.SnapshotChunk, guest.Epoch)).code);
        Ready(guest, guest.Epoch);
        // Consume both the broadcast failure and the refusal to turn that same epoch green.
        await guest.WaitAsync(m => m.type == MessageTypes.SyncReady && !m.accepted);
        Assert.Equal(ErrorCodes.SyncInProgress, (await test.OpAsync(guest)).code);
        host.Send(new WireMessage { type = MessageTypes.SyncRequest });
        var next = await host.WaitAsync(m => m.type == MessageTypes.SyncBegin && m.syncId != guest.Epoch);
        await test.CompleteAsync(host, next.syncId, host, guest);
    }

    [Fact]
    public async Task FailedReadinessBroadcastIncludesBoundedPlainTextAndAuthenticatedPeerIdentity()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        guest.Send(new WireMessage { type = MessageTypes.SyncReady, syncId = guest.Epoch,
            opId = "failure-detail", accepted = false, clientId = "forged-id", displayName = "forged-name",
            message = "Missing TerrainData \"guid-a\"\r\n\t\0" + new string('x', 2000) });
        var failure = await host.WaitAsync(m => m.type == MessageTypes.SyncReady && !m.accepted);
        Assert.Equal(ErrorCodes.SyncFailed, failure.code);
        Assert.Equal(guest.Id, failure.clientId);
        Assert.Equal(guest.Epoch, failure.syncId);
        Assert.Equal("failure-detail", failure.opId);
        Assert.StartsWith("Recovery failed at guest (guest): Missing TerrainData \"guid-a\" ", failure.message);
        Assert.DoesNotContain("forged", failure.message);
        Assert.DoesNotContain(failure.message, char.IsControl);
        Assert.InRange(failure.message.Length, 1, 18 + 64 + 2 + 64 + 3 + 512);
        var echoed = await guest.WaitAsync(m => m.type == MessageTypes.SyncReady && m.opId == "failure-detail");
        Assert.Equal(failure.message, echoed.message);
        Assert.Equal(ErrorCodes.SyncInProgress, (await test.OpAsync(host)).code);
    }

    [Fact]
    public async Task EmptyFailureReportStillIdentifiesThePeer()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        Ready(host, host.Epoch, false);
        var failure = await host.WaitAsync(m => m.type == MessageTypes.SyncReady && !m.accepted);
        Assert.Equal("Recovery failed at host (host): peer reported unsuccessful recovery", failure.message);
    }

    [Fact]
    public async Task JoinLeaveAndHostPromotionRestartTheBarrierAndDiscardReadiness()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var oldest = await test.JoinAsync("z-oldest");
        var younger = await test.JoinAsync("a-younger");
        await test.CompleteAsync(host, younger.Epoch, host, oldest, younger);
        await LockAsync(oldest);
        var newest = await test.JoinAsync("newest");
        Assert.Empty(test.Room.State.TerrainLeases.Snapshot());
        Assert.NotEqual(younger.Epoch, newest.Epoch);
        await test.EndAsync(host, newest.Epoch);
        Ready(oldest, newest.Epoch);
        await host.DisconnectAsync();

        var promotion = await oldest.WaitAsync(m => m.type == MessageTypes.SyncBegin && m.clientId == oldest.Id);
        Assert.NotEqual(newest.Epoch, promotion.syncId);
        Assert.Equal(oldest.Id, test.Room.State.HostClientId);
        Ready(younger, newest.Epoch);
        Assert.Equal(ErrorCodes.SyncMismatch,
            (await younger.WaitAsync(m => m.type == MessageTypes.SyncReady && !m.accepted && m.syncId == newest.Epoch)).code);
        await test.CompleteAsync(oldest, promotion.syncId, oldest, younger, newest);

        await newest.DisconnectAsync();
        var leave = await oldest.WaitAsync(m => m.type == MessageTypes.SyncBegin && m.syncId != promotion.syncId);
        Assert.Equal(oldest.Id, leave.clientId);
        Assert.Equal(ErrorCodes.SyncInProgress, (await test.OpAsync(younger)).code);
        await test.CompleteAsync(oldest, leave.syncId, oldest, younger);
    }

    [Fact]
    public async Task LifecycleAndControlsWaitUntilEarlierOperationDeliveryFinishes()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        await test.CompleteAsync(host, guest.Epoch, host, guest);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        guest.Socket.BeforeSend = message =>
        {
            if (message.type != MessageTypes.Op || message.opId != "blocked-op") return Task.CompletedTask;
            entered.TrySetResult(true);
            return release.Task;
        };
        host.Send(new WireMessage { type = MessageTypes.Op, opId = "blocked-op", opKind = OpKinds.SetProperty,
            revision = test.Room.State.Revision });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var joining = test.JoinAsync("new");
        guest.Send(new WireMessage { type = MessageTypes.SyncRequest });
        Assert.Equal(2, test.Room.ConnectionCount);
        Assert.False(joining.IsCompleted);
        release.TrySetResult(true);
        var joined = await joining;
        await guest.WaitAsync(m => m.type == MessageTypes.SyncBegin && m.syncId == joined.Epoch);
        var messages = guest.Socket.Messages.ToArray();
        var opIndex = Array.FindIndex(messages, m => m.type == MessageTypes.Op && m.opId == "blocked-op");
        Assert.True(opIndex >= 0);
        Assert.All(messages.Select((m, index) => (m, index)).Where(x => x.m.type == MessageTypes.SyncBegin
                && x.m.syncId != guest.Epoch), x => Assert.True(x.index > opIndex));
    }

    [Fact]
    public async Task BrokenSendCannotPreventOtherPeersFromRecoveringAfterPromotion()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        await test.CompleteAsync(host, guest.Epoch, host, guest);
        host.Socket.BeforeSend = _ => throw new System.Net.WebSockets.WebSocketException("simulated send failure");
        guest.Send(new WireMessage { type = MessageTypes.SyncRequest });
        var promoted = await guest.WaitAsync(m => m.type == MessageTypes.SyncBegin && m.clientId == guest.Id);
        Assert.Equal(guest.Id, test.Room.State.HostClientId);
        await test.CompleteAsync(guest, promoted.syncId, guest);
        Assert.True((await test.OpAsync(guest)).accepted);
    }

    [Fact]
    public async Task OldProtocolIsRejectedBeforeMembershipOrRecoveryStarts()
    {
        await using var test = new RoomSocketFixture();
        var old = new Peer("old");
        test.Peers.Add(old);
        old.Run = test.Room.HandleAsync(old.Socket,
            new SceneCollab.Server.WebApi.WebSockets.ClientConnection(old.Socket, old.Id, "test"), CancellationToken.None);
        old.Send(new WireMessage { type = MessageTypes.Hello, protocolVersion = 1 });
        Assert.Equal(ErrorCodes.ProtocolMismatch, (await old.WaitAsync(m => m.type == MessageTypes.Error)).code);
        await old.Run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, test.Room.ConnectionCount);
        Assert.Equal(0, test.Room.State.ParticipantCount);
        Assert.DoesNotContain(old.Socket.Messages, m => m.type == MessageTypes.SyncBegin);
        var host = await test.JoinAsync("host");
        await test.CompleteAsync(host, host.Epoch, host);
    }
}
