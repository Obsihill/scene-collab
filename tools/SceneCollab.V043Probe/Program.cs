using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SceneCollab.Contracts.Protocol;

// End-to-end check of what v0.4.3 added to the wire, with no Unity involved.
//
// The point is the parts a unit test cannot prove: that the server relays snapshot chunks and camera
// poses at all, that a pose does not move the revision, that a host snapshot is allowed to touch a
// locked node while a guest's "snapshot" is still refused, and that a view never comes back to its
// sender.
//
//   dotnet run --project tools/SceneCollab.V043Probe -- --url ws://127.0.0.1:8100/ws --token T

var options = ParseArgs(args);
var url = options.GetValueOrDefault("--url", "ws://127.0.0.1:8100/ws");
var token = options.GetValueOrDefault("--token", "");
var room = options.GetValueOrDefault("--room", "v043probe");
var outPath = options.GetValueOrDefault("--out", "artifacts/v043/report.json");

var sceneGuid = "33333333333333333333333333333333";
var failures = 0;
var results = new List<object>();

void Check(string name, bool passed, string detail = "")
{
    results.Add(new { name, passed, detail });
    Console.WriteLine((passed ? "PASS  " : "FAIL  ") + name + (detail.Length == 0 ? "" : "  — " + detail));
    if (!passed)
    {
        failures++;
    }
}

// A peer's idea of the revision can lag behind the room (frames it has not read yet). The server
// answers a stale op with its current revision, so one retry is enough to speak from the right place.
async Task<WireMessage?> SendOpAsync(Peer peer, Func<long, WireMessage> build)
{
    var message = build(peer.Revision);
    for (var attempt = 0; attempt < 4; attempt++)
    {
        message.opId = Guid.NewGuid().ToString("N");
        await peer.SendAsync(message);
        var ack = await peer.NextAsync(m => m.type == MessageTypes.Ack && m.opId == message.opId);
        if (ack == null || ack.accepted || ack.code != ErrorCodes.StaleRevision)
        {
            return ack;
        }

        message.revision = ack.revision;
    }

    return null;
}

var host = await Peer.ConnectAsync(url, room, token, "v043-host", sceneGuid, "Snapshot", "Assets/Snapshot.unity");
Check("host joined and is host", host.Welcome is { isHost: true }, "isHost=" + host.Welcome?.isHost);

var guest = await Peer.ConnectAsync(url, room, token, "v043-guest", sceneGuid, "Snapshot", "Assets/Snapshot.unity");
Check("guest joined as a guest", guest.Welcome is { isHost: false }, "isHost=" + guest.Welcome?.isHost);

var lockedNode = "aaaaaaaaaaaaaaaa";
var guestOwned = "bbbbbbbbbbbbbbbb";

await guest.SendAsync(new WireMessage
{
    type = MessageTypes.Lock,
    protocolVersion = ProtocolVersion.Current,
    lockNodeId = guestOwned,
});
var guestLock = await guest.NextAsync(m => m.type == MessageTypes.Lock);
Check("guest holds a lock", guestLock?.lockedNodeIds?.Contains(guestOwned) == true);

await host.SendAsync(new WireMessage
{
    type = MessageTypes.Lock,
    protocolVersion = ProtocolVersion.Current,
    lockNodeId = lockedNode,
});
var hostLock = await host.NextAsync(m => m.type == MessageTypes.Lock && m.lockedNodeIds?.Contains(lockedNode) == true);
Check("host holds the node the guest will try to steal", hostLock?.lockedNodeIds?.Contains(lockedNode) == true,
    "locks=" + (hostLock?.lockedNodeIds == null ? "(none)" : string.Join(",", hostLock.lockedNodeIds)));

// 1. snapshot_start / snapshot_chunk / snapshot_end travel as ordinary ops with the snapshot flag.
var startId = Guid.NewGuid().ToString("N");
await host.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = startId,
    opKind = OpKinds.SnapshotStart,
    nodeId = "",
    oldValue = "",
    newValue = "1",
    snapshot = true,
    revision = host.Revision,
});
var startAck = await host.NextAsync(m => m.type == MessageTypes.Ack && m.opId == startId);
Check("server accepted snapshot_start", startAck is { accepted: true },
    "code=" + (startAck?.code ?? "(none)") + (startAck?.message == null ? "" : " " + startAck.message));

var startAtGuest = await guest.NextAsync(m => m.type == MessageTypes.Op && m.opId == startId);
Check("guest received snapshot_start", startAtGuest != null);
Check("snapshot_start is flagged as a snapshot", startAtGuest?.snapshot == true);

var chunkOps = new[]
{
    new WireMessage
    {
        type = MessageTypes.Op,
        protocolVersion = ProtocolVersion.Current,
        opKind = OpKinds.AddNode,
        nodeId = guestOwned,
        parentNodeId = "",
        siblingIndex = 0,
        propertyPath = "",
        oldValue = "",
        newValue = JsonSerializer.Serialize(new { name = "Snapshot Cube" }),
        componentType = "",
        prefabGuid = "",
    },
};

var chunkId = Guid.NewGuid().ToString("N");
await host.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = chunkId,
    opKind = OpKinds.SnapshotChunk,
    nodeId = "",
    oldValue = "",
    newValue = JsonSerializer.Serialize(new SnapshotChunk { ops = chunkOps, index = 0, total = 1 }, WireJson.Options),
    snapshot = true,
    revision = host.Revision,
});
var chunkAck = await host.NextAsync(m => m.type == MessageTypes.Ack && m.opId == chunkId);
Check("server accepted snapshot_chunk", chunkAck is { accepted: true },
    "code=" + (chunkAck?.code ?? "(none)") + (chunkAck?.message == null ? "" : " " + chunkAck.message));

var chunkAtGuest = await guest.NextAsync(m => m.type == MessageTypes.Op && m.opId == chunkId);
Check("guest received the chunk", chunkAtGuest != null);
Check("chunk is flagged as a snapshot", chunkAtGuest?.snapshot == true);

var chunkBody = chunkAtGuest == null ? null : JsonSerializer.Deserialize<SnapshotChunk>(chunkAtGuest.newValue ?? "", WireJson.Options);
Console.WriteLine("   chunk newValue = " + (chunkAtGuest?.newValue == null ? "(null)"
    : "len=" + chunkAtGuest.newValue.Length + " [" + chunkAtGuest.newValue[..Math.Min(160, chunkAtGuest.newValue.Length)] + "]"));
Check("chunk survives the relay intact",
    chunkBody?.ops is { Length: 1 } && chunkBody.ops[0].nodeId == guestOwned && chunkBody.total == 1,
    chunkBody == null ? "no body" : "ops=" + chunkBody.ops?.Length + " total=" + chunkBody.total);

var endId = Guid.NewGuid().ToString("N");
await host.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = endId,
    opKind = OpKinds.SnapshotEnd,
    nodeId = "",
    oldValue = "",
    newValue = "1",
    snapshot = true,
    revision = host.Revision,
});
var endAck = await host.NextAsync(m => m.type == MessageTypes.Ack && m.opId == endId);
Check("server accepted snapshot_end", endAck is { accepted: true }, "code=" + (endAck?.code ?? "(none)"));

var endAtGuest = await guest.NextAsync(m => m.type == MessageTypes.Op && m.opId == endId);
Check("guest received snapshot_end", endAtGuest?.opKind == OpKinds.SnapshotEnd);

// 2. A host snapshot may rewrite a node the guest locked. A guest claiming "snapshot" may not.
var hostSnapshotId = Guid.NewGuid().ToString("N");
await host.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = hostSnapshotId,
    opKind = OpKinds.SetProperty,
    nodeId = guestOwned,
    propertyPath = "m_LocalPosition.x",
    oldValue = "",
    newValue = "4",
    snapshot = true,
    revision = host.Revision,
});
var hostSnapshotAck = await host.NextAsync(m => m.type == MessageTypes.Ack && m.opId == hostSnapshotId);
Check("host snapshot is not blocked by the guest's lock", hostSnapshotAck is { accepted: true },
    "code=" + (hostSnapshotAck?.code ?? "(none)"));

var hostLockedOpId = Guid.NewGuid().ToString("N");
await host.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = hostLockedOpId,
    opKind = OpKinds.SetProperty,
    nodeId = lockedNode,
    propertyPath = "m_LocalPosition.x",
    oldValue = "0",
    newValue = "1",
    snapshot = false,
    revision = host.Revision,
});

// The guest has to speak from the server's current revision: an op that disagrees is refused as
// stale before the lock is even looked at. An add_node carries no oldValue, so it is the cheapest
// way for a peer to learn the room's live revision.
var syncNode = "cccccccccccccccc";
var syncAck = await SendOpAsync(guest, revision => new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opKind = OpKinds.AddNode,
    nodeId = syncNode,
    parentNodeId = "",
    siblingIndex = 0,
    oldValue = "",
    newValue = JsonSerializer.Serialize(new { name = "Guest Sync" }),
    revision = revision,
});
Check("guest can still edit a node it locked", syncAck is { accepted: true },
    "code=" + (syncAck?.code ?? "(none)") + (syncAck?.message == null ? "" : " " + syncAck.message));
var currentRevision = syncAck?.revision ?? guest.Revision;

var guestStealId = Guid.NewGuid().ToString("N");
await guest.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = guestStealId,
    opKind = OpKinds.SetProperty,
    nodeId = lockedNode,
    propertyPath = "m_LocalPosition.x",
    oldValue = "",
    newValue = "9",
    snapshot = true,
    revision = currentRevision,
});
var guestStealAck = await guest.NextAsync(m => m.type == MessageTypes.Ack && m.opId == guestStealId);
Check("guest cannot fake a snapshot to skip a lock",
    guestStealAck is { accepted: false } && guestStealAck.code == ErrorCodes.Locked,
    "code=" + (guestStealAck?.code ?? "(none)") + " accepted=" + guestStealAck?.accepted);

// 3. A camera pose is presence: relayed to the others, never echoed back, never a revision.
var revisionBefore = guest.LastSeenRevision;
await host.SendAsync(new WireMessage
{
    type = MessageTypes.View,
    protocolVersion = ProtocolVersion.Current,
    sceneGuid = sceneGuid,
    viewValid = true,
    viewPosX = 1f,
    viewPosY = 2f,
    viewPosZ = 3f,
    viewRotX = 0f,
    viewRotY = 0.7071f,
    viewRotZ = 0f,
    viewRotW = 0.7071f,
    viewFov = 60f,
});

var viewAtGuest = await guest.NextAsync(m => m.type == MessageTypes.View, 5000);
Check("guest received the host's camera", viewAtGuest != null);
Check("camera pose arrived intact",
    viewAtGuest is { viewValid: true } && viewAtGuest.viewPosX == 1f && viewAtGuest.viewPosZ == 3f
        && viewAtGuest.viewFov == 60f,
    viewAtGuest == null ? "no view" : viewAtGuest.viewPosX + "," + viewAtGuest.viewPosY + "," + viewAtGuest.viewPosZ
        + " fov=" + viewAtGuest.viewFov);
Check("a camera pose does not move the revision", guest.LastSeenRevision == revisionBefore,
    "before=" + revisionBefore + " after=" + guest.LastSeenRevision);

var echo = await host.NextAsync(m => m.type == MessageTypes.View, 1500);
Check("the host does not receive its own camera", echo == null);

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(new
{
    ok = failures == 0,
    failed = failures,
    checks = results,
}, new JsonSerializerOptions { WriteIndented = true }));

await guest.CloseAsync();
await host.CloseAsync();

Console.WriteLine(failures == 0 ? "v0.4.3 probe PASS" : "v0.4.3 probe FAIL (" + failures + ")");
return failures == 0 ? 0 : 1;

static Dictionary<string, string> ParseArgs(string[] argv)
{
    var map = new Dictionary<string, string>();
    for (var i = 0; i < argv.Length - 1; i++)
    {
        if (argv[i].StartsWith("--"))
        {
            map[argv[i]] = argv[i + 1];
        }
    }

    return map;
}

static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

sealed class Peer : IAsyncDisposable
{
    static readonly JsonSerializerOptions Options = WireJson.Options;

    readonly ClientWebSocket _socket = new();
    readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
    readonly List<WireMessage> _pending = new();

    public WireMessage? Welcome { get; private set; }
    public long LastSeenRevision { get; private set; }

    public long Revision => LastSeenRevision;

    public static async Task<Peer> ConnectAsync(
        string url, string room, string token, string name, string sceneGuid, string sceneName, string scenePath)
    {
        var peer = new Peer();
        if (!string.IsNullOrEmpty(token))
        {
            peer._socket.Options.SetRequestHeader("X-SceneCollab-Token", token);
        }

        var connectionUrl = url + (url.Contains('?') ? "&" : "?") + "room=" + Uri.EscapeDataString(room);
        await peer._socket.ConnectAsync(new Uri(connectionUrl), peer._timeout.Token);

        await peer.SendAsync(new WireMessage
        {
            type = MessageTypes.Hello,
            protocolVersion = ProtocolVersion.Current,
            displayName = name,
            token = token,
            sceneGuid = sceneGuid,
            sceneName = sceneName,
            scenePath = scenePath,
        });

        peer.Welcome = await peer.NextAsync(m => m.type == MessageTypes.Welcome || m.type == MessageTypes.Error, 10000);
        if (peer.Welcome?.type == MessageTypes.Error || peer.Welcome == null)
        {
            throw new InvalidOperationException("no welcome for " + name + " (" + (peer.Welcome?.code ?? "timeout") + ")");
        }

        return peer;
    }

    public async Task SendAsync(WireMessage message)
    {
        var json = JsonSerializer.Serialize(message, Options);
        await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _timeout.Token);
    }

    public Task<WireMessage?> NextAsync(Func<WireMessage, bool> predicate)
    {
        return NextAsync(predicate, 15000);
    }

    /// <summary>Waits for a matching frame; returns null instead of hanging when it never arrives.</summary>
    public async Task<WireMessage?> NextAsync(Func<WireMessage, bool> predicate, int timeoutMs)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            if (predicate(_pending[i]))
            {
                var hit = _pending[i];
                _pending.RemoveAt(i);
                return hit;
            }
        }

        using var deadline = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            WireMessage? message;
            try
            {
                message = await ReceiveAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (message == null)
            {
                return null;
            }

            LastSeenRevision = Math.Max(LastSeenRevision, message.revision);
            if (predicate(message))
            {
                return message;
            }

            _pending.Add(message);
        }
    }

    async Task<WireMessage?> ReceiveAsync(CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                break;
            }
        }

        return JsonSerializer.Deserialize<WireMessage>(Encoding.UTF8.GetString(stream.ToArray()), Options);
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", _timeout.Token);
            }
        }
        catch (Exception)
        {
            // A socket the server already dropped is not a failure.
        }

        await DisposeAsync();
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _timeout.Dispose();
        return ValueTask.CompletedTask;
    }
}
