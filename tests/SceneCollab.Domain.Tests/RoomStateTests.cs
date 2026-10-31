using System;
using System.Threading;
using System.Threading.Tasks;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain.Room;
using Xunit;

namespace SceneCollab.Domain.Tests
{
    /// <summary>
    /// The conflict model is the whole product, so it is tested at the level where it is
    /// decided: an authoritative revision counter plus a single lock.
    /// </summary>
    public sealed class RoomStateTests
    {
        static WireMessage NewOp(string clientId, long revision, string opId = null, string kind = OpKinds.SetProperty)
        {
            return new WireMessage
            {
                type = MessageTypes.Op,
                clientId = clientId,
                opId = opId ?? Guid.NewGuid().ToString("N"),
                opKind = kind,
                nodeId = "node-1",
                propertyPath = "m_LocalPosition.x",
                oldValue = "0",
                newValue = "1",
                revision = revision,
            };
        }

        [Fact]
        public void FirstJoinerBecomesScribe()
        {
            var room = new RoomState();

            var participant = room.Join("client-a", "Alice", null);

            Assert.True(participant.isScribe);
            Assert.Equal("client-a", room.ScribeClientId);
        }

        [Fact]
        public void SecondJoinerIsNotScribe()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);

            var second = room.Join("client-b", "Bob", null);

            Assert.False(second.isScribe);
            Assert.Equal("client-a", room.ScribeClientId);
            Assert.Equal(2, room.ParticipantCount);
        }

        [Fact]
        public void DisplayNameFallsBackToClientId()
        {
            var room = new RoomState();

            var participant = room.Join("client-a", "   ", null);

            Assert.Equal("client-a", participant.displayName);
        }

        [Fact]
        public void ParticipantsGetDistinctColours()
        {
            var room = new RoomState();

            var a = room.Join("client-a", "Alice", null);
            var b = room.Join("client-b", "Bob", null);

            Assert.NotEqual(a.color, b.color);
        }

        [Fact]
        public void AcceptedOpIncrementsRevision()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);

            var result = room.Apply(NewOp("client-a", 0, kind: OpKinds.AddNode));

            Assert.True(result.Accepted);
            Assert.False(result.Duplicate);
            Assert.Equal(1, result.Revision);
            Assert.Equal(1, room.Revision);
            Assert.Equal("client-a", result.Record.ClientId);
            Assert.Equal(OpKinds.AddNode, result.Record.Kind);
        }

        [Fact]
        public void TwoOpsAtTheSameRevision_OnlyOneWins()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);
            room.Join("client-b", "Bob", null);

            var first = room.Apply(NewOp("client-a", 0));
            var second = room.Apply(NewOp("client-b", 0));

            Assert.True(first.Accepted);
            Assert.False(second.Accepted);
            Assert.Equal(ErrorCodes.StaleRevision, second.Code);

            // A rejection must carry the current revision so the loser can resync
            // without another round trip.
            Assert.Equal(1, second.Revision);
            Assert.Equal(1, room.Revision);
        }

        [Fact]
        public void ReplayedOpIdIsIdempotent()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);

            var first = room.Apply(NewOp("client-a", 0, opId: "fixed-op"));
            var replay = room.Apply(NewOp("client-a", 0, opId: "fixed-op"));

            Assert.True(first.Accepted);
            Assert.True(replay.Accepted);
            Assert.True(replay.Duplicate);
            Assert.Equal(1, room.Revision);
        }

        [Fact]
        public void UnknownClientIsRejected()
        {
            var room = new RoomState();

            var result = room.Apply(NewOp("stranger", 0));

            Assert.False(result.Accepted);
            Assert.Equal(ErrorCodes.NotInitialized, result.Code);
        }

        [Fact]
        public void OpWithoutIdIsMalformed()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);

            var message = NewOp("client-a", 0);
            message.opId = null;

            var result = room.Apply(message);

            Assert.False(result.Accepted);
            Assert.Equal(ErrorCodes.Malformed, result.Code);
        }

        [Fact]
        public void NullMessageIsMalformed()
        {
            var room = new RoomState();

            var result = room.Apply(null);

            Assert.False(result.Accepted);
            Assert.Equal(ErrorCodes.Malformed, result.Code);
        }

        [Fact]
        public void ScribeIsReelectedWhenTheScribeLeaves()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);
            room.Join("client-b", "Bob", null);

            Assert.True(room.Leave("client-a"));

            Assert.Equal("client-b", room.ScribeClientId);
            Assert.True(room.SnapshotParticipants()[0].isScribe);
        }

        [Fact]
        public void ScribeBecomesNullWhenEveryoneLeaves()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);

            room.Leave("client-a");

            Assert.Null(room.ScribeClientId);
            Assert.Equal(0, room.ParticipantCount);
        }

        [Fact]
        public void PresenceRoundTripsSelections()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);

            Assert.True(room.UpdatePresence("client-a", new[] { "node-1", "node-2" }));

            var participant = room.SnapshotParticipants()[0];
            Assert.Equal(2, participant.selectedNodeIds.Length);
            Assert.Equal("node-2", participant.selectedNodeIds[1]);
        }

        [Fact]
        public void SnapshotDoesNotLeakInternalState()
        {
            var room = new RoomState();
            room.Join("client-a", "Alice", null);

            var first = room.SnapshotParticipants()[0];
            first.selectedNodeIds = new[] { "mutated" };

            var second = room.SnapshotParticipants()[0];
            Assert.Empty(second.selectedNodeIds);
        }

        [Fact]
        public void HistoryIsBoundedSoLongSessionsDoNotLeak()
        {
            var room = new RoomState(historyLimit: 5);
            room.Join("client-a", "Alice", null);

            for (var i = 0; i < 10; i++)
            {
                Assert.True(room.Apply(NewOp("client-a", room.Revision)).Accepted);
            }

            Assert.Equal(10, room.Revision);
            Assert.Equal(5, room.SnapshotHistory().Length);
        }

        [Fact]
        public void ParallelAppliesNeverLoseOrDoubleCount()
        {
            const int total = 200;
            var room = new RoomState();
            room.Join("client-a", "Alice", null);

            var accepted = 0;

            Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
            {
                while (true)
                {
                    var result = room.Apply(NewOp("client-a", room.Revision));

                    if (result.Accepted)
                    {
                        Interlocked.Increment(ref accepted);
                        return;
                    }

                    Assert.Equal(ErrorCodes.StaleRevision, result.Code);
                }
            });

            Assert.Equal(total, accepted);
            Assert.Equal(total, room.Revision);
        }
    }
}
