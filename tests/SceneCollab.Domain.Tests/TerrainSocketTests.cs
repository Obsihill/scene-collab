using SceneCollab.Contracts.Protocol;
using static SceneCollab.Domain.Tests.RoomSocketFixture;

namespace SceneCollab.Domain.Tests;

public sealed class TerrainSocketTests
{
    [Fact]
    public async Task FourEditorsCanPatchIndependentRegionsWithAuthenticatedOwnership()
    {
        await using var test = new RoomSocketFixture();
        var peers = new Peer[4];
        for (var i = 0; i < peers.Length; i++) peers[i] = await test.JoinAsync("peer" + i);
        await test.CompleteAsync(peers[0], peers[3].Epoch, peers);

        for (var i = 0; i < peers.Length; i++)
        {
            var reply = await LockAsync(peers[i], i * 4, 0, 4, 16);
            Assert.True(reply.accepted);
            var lease = reply.terrainLocks.Single(l => l.clientId == peers[i].Id);
            Assert.Equal(peers[i].Id, reply.clientId);
            Assert.Equal(lease.leaseId, reply.terrainLeaseId);
            var patch = Patch(lease.leaseId);
            patch.terrainX = i * 128;
            patch.clientId = "forged-host";
            peers[i].Send(patch);
            var ack = await peers[i].WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == patch.opId);
            Assert.True(ack.accepted);
            var receiver = peers[(i + 1) % peers.Length];
            var relayed = await receiver.WaitAsync(m => m.type == MessageTypes.Terrain && m.opId == patch.opId);
            Assert.Equal(peers[i].Id, relayed.clientId);
            Assert.Equal(lease.leaseId, relayed.terrainLeaseId);
        }
        var denial = await LockAsync(peers[1], 0, 0, 1, 1);
        Assert.False(denial.accepted);
        Assert.Equal(ErrorCodes.Locked, denial.code);
        Assert.Equal(4, denial.terrainLocks.Length);
        Assert.Equal(2, test.Room.State.Revision); // scene snapshot only; terrain does not advance it
    }

    [Fact]
    public async Task GuestPatchesRequireOneOwnedLeaseCoveringEverySectorAndRenewOnSuccess()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        await test.CompleteAsync(host, guest.Epoch, host, guest);
        var hostLease = (await LockAsync(host, 0, 0, 2, 2)).terrainLeaseId;
        var guestLease = (await LockAsync(guest, 4, 0, 1, 1)).terrainLeaseId;

        var stolen = Patch(hostLease);
        guest.Send(stolen);
        Assert.Equal(ErrorCodes.TerrainLeaseInvalid,
            (await guest.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == stolen.opId)).code);

        var outside = Patch(guestLease);
        outside.terrainX = 128;
        outside.terrainWidth = 64;
        outside.terrainPayload = Convert.ToBase64String(new byte[64 * 32 * 2]);
        guest.Send(outside);
        Assert.Equal(ErrorCodes.TerrainLeaseInvalid,
            (await guest.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == outside.opId)).code);

        test.Now = 5000;
        var valid = Patch(guestLease);
        valid.terrainX = 128;
        guest.Send(valid);
        Assert.True((await guest.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == valid.opId)).accepted);
        var snapshot = await host.WaitAsync(m => m.type == MessageTypes.TerrainLocks
            && m.terrainLocks.Any(l => l.leaseId == guestLease && l.expiresAt == 20000));
        Assert.Equal(20000, snapshot.terrainLocks.Single(l => l.leaseId == guestLease).expiresAt);
        Assert.DoesNotContain(host.Socket.Messages, m => m.type == MessageTypes.Terrain && m.opId == outside.opId);
    }

    [Fact]
    public async Task UnlockIsCorrelatedOwnerOnlyAndBroadcastsTheWholeRemainingList()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        await test.CompleteAsync(host, guest.Epoch, host, guest);
        var lease = (await LockAsync(host)).terrainLeaseId;
        var request = new WireMessage { type = MessageTypes.TerrainUnlock, opId = "unlock-stolen",
            terrainGuid = "terrain", terrainLeaseId = lease, clientId = host.Id };
        guest.Send(request);
        var denied = await guest.WaitAsync(m => m.type == MessageTypes.TerrainLocks && m.opId == request.opId);
        Assert.False(denied.accepted);
        Assert.Single(denied.terrainLocks);
        request.opId = "unlock-owned";
        host.Send(request);
        var granted = await guest.WaitAsync(m => m.type == MessageTypes.TerrainLocks && m.opId == request.opId);
        Assert.True(granted.accepted);
        Assert.Empty(granted.terrainLocks);
    }

    [Fact]
    public async Task ExpiryAndDisconnectBroadcastLeaseRemovalAndReconnectCannotReuseItsLease()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        await test.CompleteAsync(host, guest.Epoch, host, guest);
        var expired = (await LockAsync(guest)).terrainLeaseId;
        test.Now += TerrainRegionRules.LeaseDurationMs;
        var patch = Patch(expired);
        guest.Send(patch);
        Assert.Equal(ErrorCodes.TerrainLeaseInvalid,
            (await guest.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == patch.opId)).code);
        Assert.Empty((await host.WaitAsync(m => m.type == MessageTypes.TerrainLocks && m.terrainLocks.Length == 0)).terrainLocks);

        var abandoned = (await LockAsync(guest)).terrainLeaseId;
        await guest.DisconnectAsync();
        await host.WaitAsync(m => m.type == MessageTypes.TerrainLocks && m.terrainLocks.Length == 0);
        Assert.Empty(test.Room.State.TerrainLeases.Snapshot());
        var reconnect = await test.JoinAsync("guest-new");
        await test.CompleteAsync(host, reconnect.Epoch, host, reconnect);
        var oldPatch = Patch(abandoned);
        reconnect.Send(oldPatch);
        Assert.Equal(ErrorCodes.TerrainLeaseInvalid,
            (await reconnect.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == oldPatch.opId)).code);
    }

    public static IEnumerable<object[]> MalformedPatches()
    {
        yield return new object[] { "base64", (Action<WireMessage>)(p => p.terrainPayload = new string('!', p.terrainPayload.Length)) };
        yield return new object[] { "truncated", (Action<WireMessage>)(p => p.terrainPayload = "AAAA") };
        yield return new object[] { "extra-bytes", (Action<WireMessage>)(p => p.terrainPayload = Convert.ToBase64String(new byte[2049])) };
        yield return new object[] { "oversized", (Action<WireMessage>)(p => p.terrainPayload = new string('A', 65540)) };
        yield return new object[] { "kind", (Action<WireMessage>)(p => p.terrainKind = "holes") };
        yield return new object[] { "height-layers", (Action<WireMessage>)(p => p.terrainLayers = 1) };
        yield return new object[] { "splat-layers", (Action<WireMessage>)(p => { p.terrainKind = "splat"; p.terrainLayers = -1; }) };
        yield return new object[] { "overflow", (Action<WireMessage>)(p => { p.terrainResolution = int.MaxValue;
            p.terrainWidth = p.terrainHeight = p.terrainLayers = int.MaxValue; p.terrainKind = "splat"; }) };
        yield return new object[] { "bounds-overflow", (Action<WireMessage>)(p => { p.terrainX = int.MaxValue; p.terrainWidth = int.MaxValue; }) };
        yield return new object[] { "negative", (Action<WireMessage>)(p => p.terrainZ = -1) };
        yield return new object[] { "zero", (Action<WireMessage>)(p => p.terrainWidth = 0) };
        yield return new object[] { "resolution", (Action<WireMessage>)(p => p.terrainResolution = 0) };
        yield return new object[] { "op-id", (Action<WireMessage>)(p => p.opId = null) };
    }

    [Theory]
    [MemberData(nameof(MalformedPatches))]
    public async Task MalformedPatchesGetNegativeAckDoNotRelayOrRenewAndDoNotStall(string name, Action<WireMessage> mutate)
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        await test.CompleteAsync(host, guest.Epoch, host, guest);
        var lease = (await LockAsync(guest)).terrainLeaseId;
        test.Now += 1000;
        var malformed = Patch(lease);
        malformed.opId = name;
        mutate(malformed);
        guest.Send(malformed);
        var reply = await guest.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == malformed.opId);
        Assert.False(reply.accepted);
        Assert.Equal(ErrorCodes.Malformed, reply.code);
        Assert.Equal(16000, Assert.Single(test.Room.State.TerrainLeases.Snapshot()).expiresAt);
        Assert.DoesNotContain(host.Socket.Messages, m => m.type == MessageTypes.Terrain && m.opId == malformed.opId);

        guest.SendJson("{ malformed json");
        Assert.Equal(ErrorCodes.Malformed, (await guest.WaitAsync(m => m.type == MessageTypes.Error)).code);
        var valid = Patch(lease);
        guest.Send(valid);
        Assert.True((await guest.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == valid.opId)).accepted);
    }

    [Fact]
    public async Task ValidSplatPayloadCanBeSentByAGuestWithALease()
    {
        await using var test = new RoomSocketFixture();
        var host = await test.JoinAsync("host");
        var guest = await test.JoinAsync("guest");
        await test.CompleteAsync(host, guest.Epoch, host, guest);
        var patch = Patch((await LockAsync(guest)).terrainLeaseId);
        patch.terrainKind = "splat";
        patch.terrainLayers = 3;
        patch.terrainPayload = Convert.ToBase64String(new byte[32 * 32 * 3]);
        guest.Send(patch);
        Assert.True((await guest.WaitAsync(m => m.type == MessageTypes.TerrainAck && m.opId == patch.opId)).accepted);
        Assert.Equal(3, (await host.WaitAsync(m => m.type == MessageTypes.Terrain && m.opId == patch.opId)).terrainLayers);
    }
}
