using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SceneCollab.Contracts.Protocol;

// End-to-end check of the v0.3 host model, without Unity.
//
// The v0.2 harness proved "one property edit moves both ways". This one is about the things that
// only exist in v0.3 and cannot be checked by a unit test: the scene handshake, the /rooms listing,
// lock ownership, the asset manifest, and what happens to authority when the host leaves.
//
// Everything is asserted against a real server over real sockets, and the report is written to
// --out so the result survives the log.
//
//   dotnet run --project tools/SceneCollab.V03Probe -- --url ws://127.0.0.1:8100/ws --token T

var options = ParseArgs(args);
var url = options.GetValueOrDefault("--url", "ws://127.0.0.1:8100/ws");
var token = options.GetValueOrDefault("--token", "");
var room = options.GetValueOrDefault("--room", "v03probe");
var outPath = options.GetValueOrDefault("--out", "artifacts/v03/report.json");

// Two entry points share the wire code: the self-contained protocol probe, and the guest half of
// the Unity round trip that the batch-mode editor harness waits for.
if (options.TryGetValue("--guest-dir", out var guestDir))
{
    return await SceneCollab.V03Probe.GuestFlow.RunAsync(guestDir, url, token, room);
}

var guidA = "11111111111111111111111111111111";
var guidB = "22222222222222222222222222222222";
var assets = new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "cccccccccccccccccccccccccccccccc" };
var nodeId = "node-locked-1";

var results = new List<object>();
var failures = 0;

void Check(string name, bool passed, string detail = "")
{
    results.Add(new { name, passed, detail });
    Console.WriteLine((passed ? "PASS  " : "FAIL  ") + name + (detail.Length == 0 ? "" : "  — " + detail));
    if (!passed)
    {
        failures++;
    }
}

var httpBase = url.EndsWith("/ws", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;
httpBase = httpBase.Replace("ws://", "http://").Replace("wss://", "https://");

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
if (!string.IsNullOrEmpty(token))
{
    http.DefaultRequestHeaders.Add("X-SceneCollab-Token", token);
}

async Task<JsonElement> GetJson(string path)
{
    var body = await http.GetStringAsync(httpBase + path);
    return JsonDocument.Parse(body).RootElement.Clone();
}

// ---------------------------------------------------------------- 1. host joins with a scene

var host = await Peer.ConnectAsync(url, room, token, "probe-host", guidA, "Sample.unity", "Assets/Sample.unity");
Check("host welcome says host", host.Welcome is { isHost: true }, "isHost=" + host.Welcome?.isHost);
Check("host welcome carries the scene guid", host.Welcome?.sceneGuid == guidA, host.Welcome?.sceneGuid ?? "(null)");
Check("host welcome carries the scene name", host.Welcome?.sceneName == "Sample.unity", host.Welcome?.sceneName ?? "(null)");

// ---------------------------------------------------------------- 2. the listing other editors read

var rooms = await GetJson("/rooms");
var listed = rooms.GetProperty("rooms").EnumerateArray()
    .FirstOrDefault(r => r.GetProperty("room").GetString() == room);
Check("/rooms lists the hosted scene", listed.ValueKind == JsonValueKind.Object, "room=" + room);
Check("/rooms exposes the scene name", listed.ValueKind == JsonValueKind.Object
    && listed.GetProperty("sceneName").GetString() == "Sample.unity");
Check("/rooms names the host", listed.ValueKind == JsonValueKind.Object
    && listed.GetProperty("host").GetString() == "probe-host");

// ---------------------------------------------------------------- 3. wrong scene is refused

var mismatch = await Peer.TryConnectAsync(url, room, token, "probe-wrong", guidB, "Other.unity", "Assets/Other.unity");
Check("client with a different scene is refused",
    mismatch.Welcome == null && mismatch.ErrorCode == ErrorCodes.SceneMismatch,
    "code=" + (mismatch.ErrorCode ?? "(none)"));

// ---------------------------------------------------------------- 4. right scene, as a guest

var guest = await Peer.ConnectAsync(url, room, token, "probe-guest", guidA, "Sample.unity", "Assets/Sample.unity");
Check("second joiner is not the host", guest.Welcome is { isHost: false }, "isHost=" + guest.Welcome?.isHost);
Check("guest adopts the host's scene guid", guest.Welcome?.sceneGuid == guidA, guest.Welcome?.sceneGuid ?? "(null)");

// ---------------------------------------------------------------- 5. asset manifest

await host.SendAsync(new WireMessage
{
    type = MessageTypes.Assets,
    protocolVersion = ProtocolVersion.Current,
    assetGuids = assets,
    assetCount = assets.Length,
});
var notice = await guest.NextAsync(m => m.type == MessageTypes.Assets);
Check("guest receives the asset guid list", notice?.assetGuids?.Length == 3,
    "count=" + (notice?.assetGuids?.Length.ToString() ?? "(none)"));
Check("guids keep their order", notice?.assetGuids != null && notice.assetGuids.SequenceEqual(assets));

var late = await Peer.ConnectAsync(url, room, token, "probe-late", guidA, "Sample.unity", "Assets/Sample.unity");
Check("a later joiner gets the manifest in its welcome", late.Welcome?.assetGuids?.Length == 3,
    "count=" + (late.Welcome?.assetGuids?.Length.ToString() ?? "(none)"));

// A guest may not publish: authority over the manifest belongs to the host.
await guest.SendAsync(new WireMessage
{
    type = MessageTypes.Assets,
    protocolVersion = ProtocolVersion.Current,
    assetGuids = new[] { "dddddddddddddddddddddddddddddddd" },
});
var notHost = await guest.NextAsync(m => m.type == MessageTypes.Error && m.code == ErrorCodes.NotHost);
Check("guest cannot publish the manifest", notHost != null, "code=" + (notHost?.code ?? "(none)"));

// ---------------------------------------------------------------- 6. locks

await guest.SendAsync(new WireMessage
{
    type = MessageTypes.Lock,
    protocolVersion = ProtocolVersion.Current,
    lockNodeId = nodeId,
});
var hostLockView = await host.NextAsync(m => m.type == MessageTypes.Lock);
var guestLockView = await guest.NextAsync(m => m.type == MessageTypes.Lock);
Check("lock request echoes the full lock set to the host",
    hostLockView?.lockedNodeIds?.Contains(nodeId) == true);
Check("lock request echoes the full lock set to the guest",
    guestLockView?.lockedNodeIds?.Contains(nodeId) == true);

// ---------------------------------------------------------------- 7. structural op relay

var structuralOpId = Guid.NewGuid().ToString("N");
await guest.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = structuralOpId,
    opKind = OpKinds.AddNode,
    nodeId = nodeId,
    oldValue = "",
    newValue = "Probe Object",
    parentNodeId = "",
    siblingIndex = 2,
    componentType = "UnityEngine.Light",
    prefabGuid = "ffffffffffffffffffffffffffffffff",
    revision = guest.Revision,
});

var ack = await guest.NextAsync(m => m.type == MessageTypes.Ack && m.opId == structuralOpId);
Check("structural op from the lock holder is accepted", ack is { accepted: true },
    "code=" + (ack?.code ?? "(none)") + (ack?.message == null ? "" : " " + ack.message));

var relayed = await host.NextAsync(m => m.type == MessageTypes.Op && m.opId == structuralOpId);
Check("host receives the structural op", relayed != null);
Check("relay keeps the sibling index", relayed?.siblingIndex == 2, "siblingIndex=" + relayed?.siblingIndex);
Check("relay keeps the component type", relayed?.componentType == "UnityEngine.Light", relayed?.componentType ?? "(null)");
Check("relay keeps the prefab guid", relayed?.prefabGuid == "ffffffffffffffffffffffffffffffff");

// The same node is off limits to everyone else while the guest holds the lock.

var blockedOpId = Guid.NewGuid().ToString("N");
await host.SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = blockedOpId,
    opKind = OpKinds.RemoveNode,
    nodeId = nodeId,
    oldValue = "",
    newValue = "",
    revision = host.Revision,
});

var blocked = await host.NextAsync(m => m.type == MessageTypes.Ack && m.opId == blockedOpId);
Check("other editors are refused on a locked node", blocked is { accepted: false } && blocked.code == ErrorCodes.Locked,
    "code=" + (blocked?.code ?? "(none)"));

// ---------------------------------------------------------------- 8. host handover

await host.CloseAsync();
var handover = await guest.NextAsync(m => m.type == MessageTypes.Presence && m.participants != null
    && m.participants.Any(p => p.displayName == "probe-guest" && p.isHost));
Check("host role is handed to a remaining participant", handover != null);

var afterRooms = await GetJson("/rooms");
var after = afterRooms.GetProperty("rooms").EnumerateArray()
    .FirstOrDefault(r => r.GetProperty("room").GetString() == room);
Check("/rooms shows the new host", after.ValueKind == JsonValueKind.Object
    && after.GetProperty("host").GetString() == "probe-guest",
    "host=" + (after.ValueKind == JsonValueKind.Object ? after.GetProperty("host").GetString() : "(none)"));

await guest.CloseAsync();
await late.CloseAsync();

Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, JsonSerializer.Serialize(new
{
    ranAt = DateTime.UtcNow.ToString("o"),
    url,
    room,
    passed = results.Count - failures,
    failed = failures,
    checks = results,
}, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine();
Console.WriteLine(failures == 0
    ? "PASS: host model holds (" + results.Count + " checks)"
    : "FAIL: " + failures + " of " + results.Count + " checks failed");
return failures == 0 ? 0 : 1;

static Dictionary<string, string> ParseArgs(string[] raw)
{
    var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < raw.Length - 1; i++)
    {
        if (raw[i].StartsWith("--", StringComparison.Ordinal))
        {
            parsed[raw[i]] = raw[i + 1];
            i++;
        }
    }

    return parsed;
}

sealed class Peer : IAsyncDisposable
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    readonly ClientWebSocket _socket = new();
    readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));
    readonly List<WireMessage> _pending = new();

    public WireMessage? Welcome { get; private set; }
    public string? ErrorCode { get; private set; }
    public long LastSeenRevision { get; private set; }

    public static async Task<Peer> ConnectAsync(
        string url, string room, string token, string name, string sceneGuid, string sceneName, string scenePath)
    {
        var peer = new Peer();
        await peer.OpenAsync(url, room, token, name, sceneGuid, sceneName, scenePath);
        if (peer.Welcome == null)
        {
            throw new InvalidOperationException("no welcome for " + name + " (error " + (peer.ErrorCode ?? "none") + ")");
        }

        return peer;
    }

    /// <summary>Connects expecting to be turned away; returns the error code instead of throwing.</summary>
    public static async Task<Peer> TryConnectAsync(
        string url, string room, string token, string name, string sceneGuid, string sceneName, string scenePath)
    {
        var peer = new Peer();
        try
        {
            await peer.OpenAsync(url, room, token, name, sceneGuid, sceneName, scenePath);
        }
        catch (InvalidOperationException)
        {
            // A refusal is the expected outcome here.
        }

        return peer;
    }

    async Task OpenAsync(
        string url, string room, string token, string name, string sceneGuid, string sceneName, string scenePath)
    {
        if (!string.IsNullOrEmpty(token))
        {
            _socket.Options.SetRequestHeader("X-SceneCollab-Token", token);
        }

        var connectionUrl = url + (url.Contains('?') ? "&" : "?") + "room=" + Uri.EscapeDataString(room);
        await _socket.ConnectAsync(new Uri(connectionUrl), _timeout.Token);

        await SendAsync(new WireMessage
        {
            type = MessageTypes.Hello,
            protocolVersion = ProtocolVersion.Current,
            displayName = name,
            token = token,
            sceneGuid = sceneGuid,
            sceneName = sceneName,
            scenePath = scenePath,
        });

        Welcome = await NextAsync(m => m.type == MessageTypes.Welcome || m.type == MessageTypes.Error);
        if (Welcome?.type == MessageTypes.Error)
        {
            ErrorCode = Welcome.code;
            Welcome = null;
        }
    }

    public async Task SendAsync(WireMessage message)
    {
        var json = JsonSerializer.Serialize(message, Options);
        await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _timeout.Token);
    }

    public async Task<WireMessage?> NextAsync(Func<WireMessage, bool> predicate)
    {
        foreach (var queued in _pending)
        {
            if (predicate(queued))
            {
                _pending.Remove(queued);
                return queued;
            }
        }

        while (true)
        {
            var message = await ReceiveAsync();
            if (message == null)
            {
                return null;
            }

            LastSeenRevision = Math.Max(LastSeenRevision, message.revision);
            Console.WriteLine("   <- " + message.type + " rev=" + message.revision
                + (message.participants == null ? "" : " people=" + string.Join(",", message.participants.Select(x => x.displayName + (x.isHost ? "*" : "")))));
            if (predicate(message))
            {
                return message;
            }

            _pending.Add(message);
        }
    }

    /// <summary>The newest room revision this peer has seen.</summary>
    public long Revision
    {
        get { return LastSeenRevision; }
    }

    async Task<WireMessage?> ReceiveAsync()
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, _timeout.Token);
            }
            catch (WebSocketException)
            {
                return null;
            }

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
            // Closing a socket that the server already dropped is not a failure.
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
