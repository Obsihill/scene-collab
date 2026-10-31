using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SceneCollab.Server.WebApi.WebSockets
{
    /// <summary>
    /// Rooms are created on first use and never eagerly. One server process can host many
    /// isolated teams; each room has its own <see cref="SceneCollab.Domain.Room.RoomState"/>.
    /// </summary>
    public sealed class SessionHub
    {
        readonly ConcurrentDictionary<string, RoomSession> _rooms =
            new ConcurrentDictionary<string, RoomSession>(System.StringComparer.Ordinal);

        public int RoomCount
        {
            get { return _rooms.Count; }
        }

        public RoomSession GetRoom(string roomKey)
        {
            return _rooms.GetOrAdd(roomKey, key => new RoomSession(key));
        }

        public IReadOnlyCollection<RoomSession> SnapshotRooms()
        {
            return new List<RoomSession>(_rooms.Values);
        }
    }
}
