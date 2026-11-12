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
        readonly object _gate = new object();
        readonly Dictionary<RoomSession, int> _leases = new Dictionary<RoomSession, int>();

        public int RoomCount
        {
            get { return _rooms.Count; }
        }

        public RoomSession GetRoom(string roomKey)
        {
            lock (_gate)
            {
                var room = _rooms.GetOrAdd(roomKey, key => new RoomSession(key));
                _leases.TryGetValue(room, out var count);
                _leases[room] = count + 1;
                return room;
            }
        }

        /// <summary>
        /// Releases a connection's room lease, including connections that never completed hello.
        /// The last lease removes the room atomically with respect to new acquisitions.
        /// </summary>
        public void ReleaseRoom(RoomSession room)
        {
            lock (_gate)
            {
                if (!_leases.TryGetValue(room, out var count))
                    throw new System.InvalidOperationException("Room lease has already been released.");
                if (count > 1)
                {
                    _leases[room] = count - 1;
                    return;
                }
                _leases.Remove(room);
                if (_rooms.TryGetValue(room.Key, out var registered) && ReferenceEquals(registered, room))
                    _rooms.TryRemove(room.Key, out _);
            }
        }

        public IReadOnlyCollection<RoomSession> SnapshotRooms()
        {
            return new List<RoomSession>(_rooms.Values);
        }
    }
}
