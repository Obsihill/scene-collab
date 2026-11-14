using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Server.WebApi.WebSockets;

namespace SceneCollab.Domain.Tests;

internal sealed class RoomSocketFixture : IAsyncDisposable
{
    readonly CancellationTokenSource _stop = new();
    public long Now = 1000;
    public RoomSession Room { get; }
    public List<Peer> Peers { get; } = new();

    public RoomSocketFixture() { Room = new RoomSession("test", () => Interlocked.Read(ref Now)); }

    public async Task<Peer> JoinAsync(string id)
    {
        var peer = new Peer(id);
        Peers.Add(peer);
        peer.Run = Room.HandleAsync(peer.Socket, new ClientConnection(peer.Socket, id, "test"), _stop.Token);
        peer.Send(new WireMessage { type = MessageTypes.Hello, protocolVersion = ProtocolVersion.Current,
            displayName = id, sceneGuid = "scene" });
        peer.Epoch = (await peer.WaitAsync(m => m.type == MessageTypes.SyncBegin)).syncId;
        return peer;
    }

    public async Task<WireMessage> OpAsync(Peer peer, string kind = OpKinds.SetProperty, string epoch = null)
    {
        var request = new WireMessage { type = MessageTypes.Op, opId = Guid.NewGuid().ToString("N"),
            opKind = kind, revision = Room.State.Revision, snapshot = epoch != null, syncId = epoch };
        peer.Send(request);
        return await peer.WaitAsync(m => m.type == MessageTypes.Ack && m.opId == request.opId);
    }

    public async Task EndAsync(Peer host, string epoch, int terrainTiles = 0)
    {
        Assert.True((await OpAsync(host, OpKinds.SnapshotStart, epoch)).accepted);
        Assert.True((await OpAsync(host, OpKinds.SnapshotEnd, epoch)).accepted);
        host.Send(new WireMessage { type = MessageTypes.SyncEnd, syncId = epoch, syncTerrainTiles = terrainTiles });
        Assert.True((await host.WaitAsync(m => m.type == MessageTypes.SyncEnd && m.syncId == epoch)).accepted);
    }

    public async Task CompleteAsync(Peer host, string epoch, params Peer[] peers)
    {
        await EndAsync(host, epoch);
        foreach (var peer in peers) Ready(peer, epoch);
        foreach (var peer in peers)
            await peer.WaitAsync(m => m.type == MessageTypes.SyncReady && m.accepted && m.syncId == epoch);
    }

    public static void Ready(Peer peer, string epoch, bool accepted = true)
        => peer.Send(new WireMessage { type = MessageTypes.SyncReady, syncId = epoch, accepted = accepted });

    public static WireMessage Patch(string leaseId = null, string epoch = null)
        => new() { type = MessageTypes.Terrain, opId = Guid.NewGuid().ToString("N"), terrainValid = true,
            terrainGuid = "terrain", terrainKind = "height", terrainResolution = 512,
            terrainX = 0, terrainZ = 0, terrainWidth = 32, terrainHeight = 32,
            terrainPayload = Convert.ToBase64String(new byte[32 * 32 * 2]), terrainLeaseId = leaseId,
            snapshot = epoch != null, syncId = epoch };

    public static async Task<WireMessage> LockAsync(Peer peer, int x = 0, int z = 0, int width = 4, int height = 4,
        string leaseId = null)
    {
        var message = new WireMessage { type = MessageTypes.TerrainLock, opId = Guid.NewGuid().ToString("N"),
            terrainGuid = "terrain", regionX = x, regionZ = z, regionWidth = width, regionHeight = height,
            terrainLeaseId = leaseId, clientId = "forged" };
        peer.Send(message);
        return await peer.WaitAsync(m => m.type == MessageTypes.TerrainLocks && m.opId == message.opId);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var peer in Peers) peer.Socket.Abort();
        await Task.WhenAll(Peers.Select(peer => peer.Run)).WaitAsync(TimeSpan.FromSeconds(10));
        _stop.Dispose();
    }

    internal sealed class Peer
    {
        public string Id { get; }
        public string Epoch;
        public Task Run;
        public FakeSocket Socket { get; } = new();
        public Peer(string id) { Id = id; }
        public void Send(WireMessage message) => Socket.Input.Writer.TryWrite(Encoding.UTF8.GetBytes(WireJson.Serialize(message)));
        public void SendJson(string json) => Socket.Input.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        public async Task<WireMessage> WaitAsync(Func<WireMessage, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var message = await Socket.Output.Reader.ReadAsync(timeout.Token);
                if (predicate(message)) return message;
            }
        }
        public async Task DisconnectAsync()
        {
            Socket.Abort();
            await Run.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    internal sealed class FakeSocket : WebSocket
    {
        WebSocketState _state = WebSocketState.Open;
        byte[] _frame;
        int _offset;
        public readonly Channel<byte[]> Input = Channel.CreateUnbounded<byte[]>();
        public readonly Channel<WireMessage> Output = Channel.CreateUnbounded<WireMessage>();
        public readonly ConcurrentQueue<WireMessage> Messages = new();
        public Func<WireMessage, Task> BeforeSend;
        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override string SubProtocol => null;
        public override void Abort() { _state = WebSocketState.Aborted; Input.Writer.TryComplete(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token)
        { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token)
        { Abort(); return Task.CompletedTask; }
        public override void Dispose() { Abort(); }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (_frame == null) { _frame = await Input.Reader.ReadAsync(token); _offset = 0; }
            var count = Math.Min(buffer.Count, _frame.Length - _offset);
            Array.Copy(_frame, _offset, buffer.Array, buffer.Offset, count);
            _offset += count;
            var end = _offset == _frame.Length;
            if (end) _frame = null;
            return new WebSocketReceiveResult(count, WebSocketMessageType.Text, end);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            var message = WireJson.Deserialize(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count));
            if (BeforeSend != null) await BeforeSend(message).WaitAsync(token);
            Messages.Enqueue(message);
            Output.Writer.TryWrite(message);
        }
    }
}
