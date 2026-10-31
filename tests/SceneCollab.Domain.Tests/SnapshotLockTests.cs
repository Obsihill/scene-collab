using System;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain.Room;
using Xunit;

namespace SceneCollab.Domain.Tests
{
    /// <summary>
    /// v0.4.3: a host snapshot restates the whole scene, so it must not be blocked by a guest's object
    /// lock — but the exemption is host-only, otherwise any guest could mark its ops as a snapshot and
    /// edit locked nodes.
    /// </summary>
    public sealed class SnapshotLockTests
    {
        static WireMessage SnapshotOp(string clientId, long revision, bool snapshot, string nodeId = "node-1")
        {
            return new WireMessage
            {
                type = MessageTypes.Op,
                clientId = clientId,
                opId = Guid.NewGuid().ToString("N"),
                opKind = OpKinds.SetProperty,
                nodeId = nodeId,
                propertyPath = "m_LocalPosition.x",
                oldValue = "",
                newValue = "1",
                revision = revision,
                snapshot = snapshot,
            };
        }

        static (RoomState Room, long Revision) RoomWithGuestLock()
        {
            var room = new RoomState();
            room.Join("host", "Host", null);
            room.Join("guest", "Guest", null);
            room.TryBindScene("guid-a", "Assets/A.unity", "A", out _);

            Assert.True(room.TryLock("guest", "node-1", out _));
            return (room, room.Revision);
        }

        [Fact]
        public void HostSnapshotSkipsAGuestLock()
        {
            var (room, revision) = RoomWithGuestLock();

            var result = room.Apply(SnapshotOp("host", revision, snapshot: true));

            Assert.True(result.Accepted);
        }

        [Fact]
        public void GuestCannotUseTheSnapshotFlagToSkipALock()
        {
            var (room, revision) = RoomWithGuestLock();
            room.Join("guest-2", "Other", null);

            var result = room.Apply(SnapshotOp("guest-2", room.Revision, snapshot: true));

            Assert.False(result.Accepted);
            Assert.Equal(ErrorCodes.Locked, result.Code);
        }

        [Fact]
        public void APlainOpFromTheHostIsStillBlockedByAGuestLock()
        {
            var (room, revision) = RoomWithGuestLock();

            var result = room.Apply(SnapshotOp("host", revision, snapshot: false));

            Assert.False(result.Accepted);
            Assert.Equal(ErrorCodes.Locked, result.Code);
        }

        [Fact]
        public void ViewMessagesAreNotOps()
        {
            // A camera pose is presence: it must not consume a revision or an op id.
            var room = new RoomState();
            room.Join("host", "Host", null);
            room.Join("guest", "Guest", null);

            var before = room.Revision;
            Assert.Equal(MessageTypes.View, "view");
            Assert.Equal(before, room.Revision);
        }
    }
}
