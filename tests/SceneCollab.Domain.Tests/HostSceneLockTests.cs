using System;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain.Room;
using Xunit;

namespace SceneCollab.Domain.Tests
{
    /// <summary>
    /// The v0.3 session model: one host per room, one scene per room, and object locks that the
    /// server enforces rather than merely advertising.
    /// </summary>
    public sealed class HostSceneLockTests
    {
        static WireMessage NewOp(string clientId, long revision, string nodeId = "node-1", string kind = OpKinds.SetProperty)
        {
            return new WireMessage
            {
                type = MessageTypes.Op,
                clientId = clientId,
                opId = Guid.NewGuid().ToString("N"),
                opKind = kind,
                nodeId = nodeId,
                propertyPath = "m_LocalPosition.x",
                oldValue = "0",
                newValue = "1",
                revision = revision,
            };
        }

        [Fact]
        public void FirstJoinerIsHostAndScribe()
        {
            var room = new RoomState();

            var first = room.Join("client-a", "Alice", null);
            var second = room.Join("client-b", "Bob", null);

            Assert.True(first.isHost);
            Assert.True(first.isScribe);
            Assert.False(second.isHost);
            Assert.False(second.isScribe);
            Assert.Equal("client-a", room.HostClientId);
            Assert.Equal(room.HostClientId, room.ScribeClientId);
        }

        [Fact]
        public void RoomRejectsAClientEditingDifferentScene()
        {
            var room = new RoomState();

            string conflict;
            Assert.True(room.TryBindScene("guid-a", "Assets/A.unity", "A", out conflict));
            Assert.Null(conflict);

            Assert.True(room.TryBindScene("guid-a", "Assets/A.unity", "A", out conflict));

            Assert.False(room.TryBindScene("guid-b", "Assets/B.unity", "B", out conflict));
            Assert.Contains("guid-b", conflict);
            Assert.Equal("guid-a", room.SceneGuid);
        }

        [Fact]
        public void EmptyRoomMayBindANewScene()
        {
            var room = new RoomState();
            string conflict;

            room.Join("client-a", "Alice", null);
            Assert.True(room.TryBindScene("guid-a", "Assets/A.unity", "A", out conflict));
            room.Leave("client-a");

            // The session ended with the last participant, so the next host may open any scene.
            Assert.Null(room.SceneGuid);
            Assert.True(room.TryBindScene("guid-b", "Assets/B.unity", "B", out conflict));
            Assert.Equal("guid-b", room.SceneGuid);
        }

        [Fact]
        public void LockedNodeRejectsOpsFromOtherParticipants()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);
            room.Join("client-b", "Bob", null);

            string owner;
            Assert.True(room.TryLock("client-a", "node-1", out owner));
            Assert.False(room.TryLock("client-b", "node-1", out owner));
            Assert.Equal("client-a", owner);

            var rejected = room.Apply(NewOp("client-b", room.Revision));
            Assert.False(rejected.Accepted);
            Assert.Equal(ErrorCodes.Locked, rejected.Code);

            var accepted = room.Apply(NewOp("client-a", room.Revision));
            Assert.True(accepted.Accepted);
        }

        [Fact]
        public void UnlockLetsAnotherParticipantEdit()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);
            room.Join("client-b", "Bob", null);

            string owner;
            room.TryLock("client-a", "node-1", out owner);

            // Only the holder may release it.
            Assert.False(room.Unlock("client-b", "node-1"));
            Assert.True(room.Unlock("client-a", "node-1"));

            Assert.True(room.Apply(NewOp("client-b", room.Revision)).Accepted);
        }

        [Fact]
        public void LocksAreReleasedWhenTheHolderLeaves()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);
            room.Join("client-b", "Bob", null);

            string owner;
            room.TryLock("client-a", "node-1", out owner);
            room.Leave("client-a");

            Assert.Empty(room.SnapshotLocks());
            Assert.Equal("client-b", room.HostClientId);
            Assert.True(room.Apply(NewOp("client-b", room.Revision)).Accepted);
        }

        [Fact]
        public void PromotionGoesToTheLongestPresentParticipant()
        {
            var room = new RoomState();
            room.Join("client-z", "Zoe", null);
            room.Join("client-m", "Mia", null);
            room.Join("client-a", "Abe", null);

            // Sorted by client id, "client-a" would win — but the editor that has been in the scene
            // longest is the one with the scene loaded, so join order decides.
            room.Leave("client-z");

            Assert.Equal("client-m", room.HostClientId);
            Assert.Equal("client-m", room.ScribeClientId);
            Assert.True(room.Apply(NewOp("client-m", room.Revision)).Accepted);
        }

        [Fact]
        public void LocksAndSceneSurviveWhileTheSessionDoes()
        {
            var room = new RoomState();
            var host = room.Join("client-a", "Alice", null);
            room.Join("client-b", "Bob", null);

            string conflict;
            room.TryBindScene("guid-a", "Assets/A.unity", "A", out conflict);

            string owner;
            room.TryLock("client-b", "node-9", out owner);
            room.Leave("client-b");
            room.Join("client-c", "Cid", null);

            // Bob's lock died with him, but the scene binding is the session's, not the person's.
            Assert.Empty(room.SnapshotLocks());
            Assert.Equal("guid-a", room.SceneGuid);
            Assert.Equal("client-a", room.HostClientId);
            Assert.True(host.isHost);
        }
    }
}
