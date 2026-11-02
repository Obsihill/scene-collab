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
var clock = System.Diagnostics.Stopwatch.StartNew();

void Check(string name, bool passed, string detail = "")
{
    results.Add(new { name, passed, detail });
    Console.WriteLine("t+" + clock.Elapsed.TotalSeconds.ToString("0.0") + "s "
        + (passed ? "PASS  " : "FAIL  ") + name + (detail.Length == 0 ? "" : "  — " + detail));
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

if (args.Contains("--heartbeat"))
{
    Console.WriteLine("-- heartbeat (about two minutes) --");

    // Every peer is idle now, which is exactly when the server should be probing.
    var answered = await host.AnswerPingsAsync(45000);
    Check("the server pings an idle peer", answered >= 1, "answered=" + answered);
    Check("answering a ping keeps the connection open", host.State == WebSocketState.Open, "state=" + host.State);

    // A peer that is connected but never answers is the case TCP cannot see: a sleeping laptop, or a
    // proxy holding the socket open. The server has to drop it, or the room stays occupied forever.
    var mute = await Peer.ConnectAsync(
        url, room, token, "v043-mute", sceneGuid, "Snapshot", "Assets/Snapshot.unity", 240);
    Check("the silent peer joined", mute.Welcome is { isHost: false }, "isHost=" + mute.Welcome?.isHost);

    var keepAlive = host.AnswerPingsAsync(180000);
    var muteDropped = await mute.WaitForCloseAsync(150000);
    Check("a peer that stops answering is hung up on", muteDropped);

    var answeredWhileWaiting = await keepAlive;
    Check("the peer that answers is still connected", host.State == WebSocketState.Open && answeredWhileWaiting >= 1,
        "answered=" + answeredWhileWaiting + " state=" + host.State);

    await mute.DisposeAsync();
}

// 9. v0.4.7: the component slot and the reference token are relayed verbatim. The server never
//    interprets an op, but it does re-serialise it — a field the DTO does not know about is dropped
//    here, and the other editor then writes the wrong AudioSource or restores an empty material.
var identityId = Guid.NewGuid().ToString("N");
await host.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = identityId,
    opKind = OpKinds.SetProperty,
    nodeId = lockedNode,
    propertyPath = "c/UnityEngine.AudioSource#1/m_Volume",
    componentType = "UnityEngine.AudioSource",
    componentIndex = 1,
    oldValue = "1",
    newValue = "ref:0123456789abcdef0123456789abcdef:1234",
    snapshot = false,
    revision = host.Revision,
});

var identityAck = await host.NextAsync(m => m.type == MessageTypes.Ack && m.opId == identityId);
Check("server accepted a slot-addressed reference op", identityAck is { accepted: true },
    "code=" + (identityAck?.code ?? "(none)") + (identityAck?.message == null ? "" : " " + identityAck.message));

var identityAtGuest = await guest.NextAsync(m => m.type == MessageTypes.Op && m.opId == identityId);
Check("the component slot survives the relay", identityAtGuest?.componentIndex == 1,
    "componentIndex=" + (identityAtGuest?.componentIndex.ToString() ?? "(null)"));
Check("the reference token survives the relay",
    identityAtGuest?.newValue == "ref:0123456789abcdef0123456789abcdef:1234",
    identityAtGuest?.newValue ?? "(null)");
Check("the component type survives the relay", identityAtGuest?.componentType == "UnityEngine.AudioSource",
    identityAtGuest?.componentType ?? "(null)");

// 10. v0.5: a terrain patch is relayed verbatim, and only the host may send one. Terrain is an asset,
//     not a scene node, so it never enters the op log — the fields have to survive a re-serialise just
//     as the op fields above do, and the revision must not move.
var terrainGuid = "1111222233334444555566667777888a";
var terrainPayload = Convert.ToBase64String(Enumerable.Range(0, 16 * 16 * 2).Select(i => (byte)(i * 7)).ToArray());
var terrainRevisionBefore = host.Revision;

await host.SendAsync(new WireMessage
{
    type = MessageTypes.Terrain,
    protocolVersion = ProtocolVersion.Current,
    terrainValid = true,
    terrainKind = "height",
    terrainGuid = terrainGuid,
    terrainResolution = 513,
    terrainX = 32,
    terrainZ = 48,
    terrainWidth = 16,
    terrainHeight = 16,
    terrainPayload = terrainPayload,
    revision = host.Revision,
});

var terrainAtGuest = await guest.NextAsync(m => m.type == MessageTypes.Terrain, 5000);
Check("a terrain tile reaches the other editor", terrainAtGuest != null);
Check("the terrain payload survives the relay byte for byte",
    terrainAtGuest?.terrainPayload == terrainPayload, "bytes=" + (terrainAtGuest?.terrainPayload?.Length ?? 0));
Check("the terrain rectangle, kind and asset guid survive the relay",
    terrainAtGuest is { terrainKind: "height", terrainX: 32, terrainZ: 48, terrainWidth: 16, terrainHeight: 16 }
    && terrainAtGuest.terrainGuid == terrainGuid && terrainAtGuest.terrainResolution == 513,
    "kind=" + (terrainAtGuest?.terrainKind ?? "(null)") + " rect=" + (terrainAtGuest?.terrainX.ToString() ?? "?")
    + "," + (terrainAtGuest?.terrainZ.ToString() ?? "?") + " " + (terrainAtGuest?.terrainWidth.ToString() ?? "?")
    + "x" + (terrainAtGuest?.terrainHeight.ToString() ?? "?"));
Check("a terrain tile does not move the revision", host.Revision == terrainRevisionBefore,
    "revision=" + host.Revision + " before=" + terrainRevisionBefore);

await guest.SendAsync(new WireMessage
{
    type = MessageTypes.Terrain,
    protocolVersion = ProtocolVersion.Current,
    terrainValid = true,
    terrainKind = "height",
    terrainGuid = terrainGuid,
    terrainResolution = 513,
    terrainX = 0,
    terrainZ = 0,
    terrainWidth = 16,
    terrainHeight = 16,
    terrainPayload = terrainPayload,
    revision = guest.Revision,
});

var guestTerrainError = await guest.NextAsync(m => m.type == MessageTypes.Error, 5000);
Check("a guest may not sculpt", guestTerrainError?.code == ErrorCodes.NotHost,
    "code=" + (guestTerrainError?.code ?? "(none)"));

var refusedAtHost = await host.NextAsync(m => m.type == MessageTypes.Terrain, 1500);
Check("the refused tile is not relayed", refusedAtHost == null);

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
    readonly CancellationTokenSource _timeout;

    /// <summary>
    /// Frames are pulled by one background reader and parked here; waiting with a deadline then means
    /// waiting on the queue, not cancelling a receive. Cancelling a receive aborts the socket outright,
    /// which quietly killed every check that might legitimately see nothing arrive.
    /// </summary>
    readonly System.Threading.Channels.Channel<WireMessage> _inbox =
        System.Threading.Channels.Channel.CreateUnbounded<WireMessage>();

    Task _reader = Task.CompletedTask;

    public Peer(TimeSpan lifetime)
    {
        _timeout = new CancellationTokenSource(lifetime);
    }

    public WebSocketState State => _socket.State;
    readonly List<WireMessage> _pending = new();

    public WireMessage? Welcome { get; private set; }
    public long LastSeenRevision { get; private set; }

    public long Revision => LastSeenRevision;

    public static async Task<Peer> ConnectAsync(
        string url, string room, string token, string name, string sceneGuid, string sceneName, string scenePath,
        int lifetimeSeconds = 360)
    {
        var peer = new Peer(TimeSpan.FromSeconds(lifetimeSeconds));
        if (!string.IsNullOrEmpty(token))
        {
            peer._socket.Options.SetRequestHeader("X-SceneCollab-Token", token);
        }

        var connectionUrl = url + (url.Contains('?') ? "&" : "?") + "room=" + Uri.EscapeDataString(room);
        await peer._socket.ConnectAsync(new Uri(connectionUrl), peer._timeout.Token);
        peer._reader = peer.ReadLoopAsync();

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

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0)
            {
                return null;
            }

            using var wait = new CancellationTokenSource(left);
            WireMessage? message;
            try
            {
                if (!await _inbox.Reader.WaitToReadAsync(wait.Token))
                {
                    return null;
                }

                if (!_inbox.Reader.TryRead(out message))
                {
                    continue;
                }
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

    /// <summary>Drains the socket into the inbox so a waiting caller never has to own the receive call.</summary>
    async Task ReadLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        try
        {
            while (true)
            {
                var result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                stream.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(stream.ToArray());
                stream.SetLength(0);
                var message = JsonSerializer.Deserialize<WireMessage>(text, Options);
                if (message != null)
                {
                    _inbox.Writer.TryWrite(message);
                }
            }
        }
        catch (Exception)
        {
            // A hang-up is reported through the socket state; there is nothing to log here.
        }
        finally
        {
            _inbox.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Answers server liveness probes for up to <paramref name="timeoutMs"/> and reports how many it
    /// answered. Zero means the server never asked, which is the failure the heartbeat exists to avoid.
    /// </summary>
    public async Task<int> AnswerPingsAsync(int timeoutMs)
    {
        var answered = 0;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            var remaining = (int)Math.Max(500, (deadline - DateTime.UtcNow).TotalMilliseconds);
            var ping = await NextAsync(m => m.type == MessageTypes.Ping, Math.Min(remaining, 30000));
            if (ping == null)
            {
                return answered;
            }

            await SendAsync(new WireMessage { type = MessageTypes.Pong });
            answered++;
        }

        return answered;
    }

    /// <summary>True when the server hung up on this peer (close frame or an aborted TCP connection).</summary>
    public async Task<bool> WaitForCloseAsync(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (_socket.State != WebSocketState.Open)
            {
                return true;
            }

            await Task.Delay(250);
        }

        return _socket.State != WebSocketState.Open;
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

    public async ValueTask DisposeAsync()
    {
        _socket.Dispose();
        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // The reader ends on its own when the socket goes away.
        }

        _timeout.Dispose();
    }
}
