using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain.Room;

namespace SceneCollab.Domain.Tests;

public sealed class TerrainLeaseTests
{
    [Fact]
    public void FourEditorsCanOwnIndependentRegionsButCannotOverlap()
    {
        var leases = new TerrainLeaseRegistry();
        for (var i = 0; i < 4; i++)
            Assert.True(leases.TryAcquire("peer" + i, "Peer", "terrain", null,
                i * 4, 0, 4, 16, 100, out _, out _));

        Assert.False(leases.TryAcquire("fifth", "Other", "TERRAIN", null,
            3, 0, 2, 1, 100, out _, out var code));
        Assert.Equal(ErrorCodes.Locked, code);
        Assert.Equal(4, leases.Snapshot().Length);
        Assert.True(leases.TryAcquire("fifth", "Other", "different-terrain", null,
            0, 0, 16, 16, 100, out _, out _));
    }

    [Fact]
    public void ConcurrentOverlappingRequestsGrantExactlyOneWholeRectangle()
    {
        var leases = new TerrainLeaseRegistry();
        var accepted = 0;
        Parallel.For(0, 32, i =>
        {
            if (leases.TryAcquire("peer" + i, "Peer", "terrain", null,
                0, 0, 16, 16, 100, out _, out _)) Interlocked.Increment(ref accepted);
        });
        Assert.Equal(1, accepted);
        Assert.Single(leases.Snapshot());
    }

    [Fact]
    public void ExpansionIsAtomicKeepsItsIdAndIncludesTheOldRectangle()
    {
        var leases = new TerrainLeaseRegistry();
        Assert.True(leases.TryAcquire("a", "Alice", "terrain", null, 2, 2, 2, 2, 100, out var a, out _));
        Assert.True(leases.TryAcquire("b", "Bob", "terrain", null, 6, 0, 2, 8, 100, out _, out _));
        Assert.False(leases.TryAcquire("a", "Alice", "terrain", a.leaseId,
            3, 3, 4, 4, 200, out _, out var code));
        Assert.Equal(ErrorCodes.Locked, code);
        var unchanged = leases.Snapshot().Single(l => l.clientId == "a");
        Assert.Equal((2, 2, 2, 2, a.expiresAt),
            (unchanged.x, unchanged.z, unchanged.width, unchanged.height, unchanged.expiresAt));

        Assert.True(leases.TryAcquire("a", "Alice", "terrain", a.leaseId,
            4, 4, 2, 2, 200, out var expanded, out _));
        Assert.Equal(a.leaseId, expanded.leaseId);
        Assert.Equal((2, 2, 4, 4), (expanded.x, expanded.z, expanded.width, expanded.height));
        expanded.width = 16;
        Assert.Equal(4, leases.Snapshot().Single(l => l.clientId == "a").width);
    }

    [Fact]
    public void LeaseCoversTheWholePatchAndOnlyItsOwnerCanRenewOrUnlock()
    {
        var leases = new TerrainLeaseRegistry();
        leases.TryAcquire("a", "Alice", "terrain", null, 0, 0, 4, 4, 100, out var a, out _);
        Assert.False(leases.TryRenew("b", "terrain", a.leaseId, 0, 0, 1, 1, 200));
        Assert.False(leases.TryRenew("a", "terrain", a.leaseId, 3, 0, 2, 1, 200));
        Assert.False(leases.TryRenew("a", "other", a.leaseId, 0, 0, 1, 1, 200));
        Assert.False(leases.TryRelease("b", "terrain", a.leaseId, 200));
        Assert.False(leases.TryAcquire("b", "Bob", "terrain", a.leaseId,
            0, 0, 4, 4, 200, out _, out var code));
        Assert.Equal(ErrorCodes.TerrainLeaseInvalid, code);
        Assert.True(leases.TryRenew("a", "terrain", a.leaseId, 0, 0, 4, 4, 200));
        Assert.Equal(15200, Assert.Single(leases.Snapshot()).expiresAt);
        Assert.True(leases.TryRelease("a", "terrain", a.leaseId, 200));
        Assert.Empty(leases.Snapshot());
    }

    [Fact]
    public void ExpiryAtDeadlineReleasesTheRegionAndCannotBeRenewed()
    {
        var leases = new TerrainLeaseRegistry();
        leases.TryAcquire("a", "Alice", "terrain", null, 0, 0, 4, 4, 100, out var a, out _);
        Assert.False(leases.Expire(15099));
        Assert.False(leases.TryRenew("a", "terrain", a.leaseId, 0, 0, 1, 1, 15100));
        Assert.Empty(leases.Snapshot());
        Assert.True(leases.TryAcquire("b", "Bob", "terrain", null, 0, 0, 4, 4, 15100, out var b, out _));
        Assert.NotEqual(a.leaseId, b.leaseId);
    }

    [Fact]
    public void DisconnectReleasesLeasesAndReconnectUsesANewIdentity()
    {
        var room = new RoomState();
        room.Join("old", "Alice", null);
        room.TerrainLeases.TryAcquire("old", "Alice", "terrain", null,
            0, 0, 16, 16, 100, out var old, out _);
        room.Leave("old");
        Assert.Empty(room.TerrainLeases.Snapshot());
        room.Join("new", "Alice", null);
        Assert.False(room.TerrainLeases.TryRenew("new", "terrain", old.leaseId, 0, 0, 16, 16, 101));
    }

    [Fact]
    public void UnknownOrExpiredLeaseIdCannotBeUsedForExpansion()
    {
        var leases = new TerrainLeaseRegistry();
        Assert.False(leases.TryAcquire("a", "Alice", "terrain", "invented",
            0, 0, 1, 1, 100, out _, out var code));
        Assert.Equal(ErrorCodes.TerrainLeaseInvalid, code);
        Assert.True(leases.TryAcquire("a", "Alice", "terrain", null,
            0, 0, 1, 1, 100, out var lease, out _));
        Assert.False(leases.TryAcquire("a", "Alice", "terrain", lease.leaseId,
            0, 0, 2, 2, 15100, out _, out code));
        Assert.Equal(ErrorCodes.TerrainLeaseInvalid, code);
        Assert.Empty(leases.Snapshot());
    }

    [Theory]
    [InlineData(16, 0, 0, 1, 1, 0, 0, 1, 1)]
    [InlineData(512, 128, 256, 32, 64, 4, 8, 1, 2)]
    [InlineData(513, 128, 128, 128, 128, 3, 3, 5, 5)]
    [InlineData(513, 512, 512, 1, 1, 15, 15, 1, 1)]
    [InlineData(int.MaxValue, 0, 0, int.MaxValue, int.MaxValue, 0, 0, 16, 16)]
    public void MappingConservativelyCoversAllNormalizedSectors(int resolution, int x, int z, int width, int height,
        int expectedX, int expectedZ, int expectedWidth, int expectedHeight)
    {
        Assert.True(TerrainRegionRules.TryMapPatch(resolution, x, z, width, height,
            out var rx, out var rz, out var rw, out var rh));
        Assert.Equal((expectedX, expectedZ, expectedWidth, expectedHeight), (rx, rz, rw, rh));
    }

    [Theory]
    [InlineData(0, 0, 0, 1, 1)]
    [InlineData(16, -1, 0, 1, 1)]
    [InlineData(16, 0, 0, 0, 1)]
    [InlineData(16, 16, 0, 1, 1)]
    [InlineData(int.MaxValue, int.MaxValue - 1, 0, int.MaxValue, 1)]
    public void InvalidOrOverflowingSampleBoundsAreRejected(int resolution, int x, int z, int width, int height)
    {
        Assert.False(TerrainRegionRules.TryMapPatch(resolution, x, z, width, height, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(16, 0, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(1, 0, int.MaxValue, 1)]
    public void InvalidLeaseBoundsCannotCreatePartialLocks(int x, int z, int width, int height)
    {
        var leases = new TerrainLeaseRegistry();
        Assert.False(leases.TryAcquire("a", "Alice", "terrain", null, x, z, width, height, 100, out _, out var code));
        Assert.Equal(ErrorCodes.Malformed, code);
        Assert.Empty(leases.Snapshot());
    }
}
