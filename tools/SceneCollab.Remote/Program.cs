using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SceneCollab.Contracts.Protocol;

// A stand-in for "the second editor" in an end-to-end check.
//
// It speaks the wire protocol without Unity, which is the whole point: the Unity side is the thing
// under test, so the other participant must not share its assumptions. Coordination is by files in
// --dir, so the two processes need no shared memory and the run is reproducible from the log alone.
//
//   dotnet run --project tools/SceneCollab.Remote -- --url ws://127.0.0.1:8100/ws --token T \
//       --room live --dir artifacts/live

var options = ParseArgs(args);
var dir = options.GetValueOrDefault("--dir", "artifacts/live");
var url = options.GetValueOrDefault("--url", "ws://127.0.0.1:8100/ws");
var room = options.GetValueOrDefault("--room", "live");
var token = options.GetValueOrDefault("--token", "");
var name = options.GetValueOrDefault("--name", "remote-peer");
var expectedFromUnity = options.GetValueOrDefault("--expect", "7,8,9");

// --soak makes this peer stay for the liveness window instead of leaving after the first exchange.
// A peer that sits still for longer than the server's silence timeout is exactly the case that used
// to drop the editor: if either side fails to answer a ping, this run says so.
var soak = options.ContainsKey("--soak");
var expectedSoak = options.GetValueOrDefault("--expect-soak", "1,2,3");

Directory.CreateDirectory(dir);

var logPath = Path.Combine(dir, "peer-log.jsonl");
void Log(string evt, params (string Key, object? Value)[] fields)
{
    var payload = new Dictionary<string, object?> { ["event"] = evt, ["at"] = DateTime.UtcNow.ToString("o") };
    foreach (var (key, value) in fields)
    {
        payload[key] = value;
    }

    File.AppendAllText(logPath, JsonSerializer.Serialize(payload) + Environment.NewLine);
    Console.WriteLine("[" + evt + "] " + JsonSerializer.Serialize(payload));
}

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(soak ? 420 : 150));
using var socket = new ClientWebSocket();
if (!string.IsNullOrEmpty(token))
{
    socket.Options.SetRequestHeader("X-SceneCollab-Token", token);
}

var connectionUrl = url + (url.Contains('?') ? "&" : "?") + "room=" + Uri.EscapeDataString(room);
await socket.ConnectAsync(new Uri(connectionUrl), timeout.Token);

await SendAsync(new WireMessage
{
    type = MessageTypes.Hello,
    protocolVersion = ProtocolVersion.Current,
    displayName = name,
    token = token,
});

var welcome = await ReadUntilAsync(MessageTypes.Welcome);
var revision = welcome.revision;
Log("joined", ("clientId", welcome.clientId), ("room", welcome.room), ("revision", revision));
File.WriteAllText(Path.Combine(dir, "peer-ready.json"),
    JsonSerializer.Serialize(new { clientId = welcome.clientId, revision }));

// Wait for the Unity editor to say which node this run is about.
var readyPath = Path.Combine(dir, "unity-ready.json");
var deadline = DateTime.UtcNow.AddSeconds(90);
while (!File.Exists(readyPath))
{
    if (DateTime.UtcNow > deadline)
    {
        Log("timeout_waiting_for_unity");
        return 1;
    }

    await Task.Delay(100, timeout.Token);
}

var ready = JsonDocument.Parse(await ReadAllTextAsync(readyPath));
var nodeId = ready.RootElement.GetProperty("nodeId").GetString();
Log("unity_ready", ("nodeId", nodeId));

// The edit the peer makes. oldValue is "0,0,0" because that is where the harness puts the cube.
var opId = Guid.NewGuid().ToString("N");
await SendAsync(new WireMessage
{
    type = MessageTypes.Op,
    protocolVersion = ProtocolVersion.Current,
    opId = opId,
    opKind = OpKinds.SetProperty,
    nodeId = nodeId,
    propertyPath = "m_LocalPosition",
    oldValue = "0,0,0",
    newValue = "3,4,5",
    revision = revision,
});

var ack = await ReadUntilAsync(MessageTypes.Ack);
if (ack.revision > revision)
{
    revision = ack.revision;
}

Log("ack", ("accepted", ack.accepted), ("duplicate", ack.duplicate), ("code", ack.code), ("revision", revision));
if (!ack.accepted)
{
    Log("op_rejected");
    return 1;
}

File.WriteAllText(Path.Combine(dir, "peer-sent.json"),
    JsonSerializer.Serialize(new { nodeId, propertyPath = "m_LocalPosition", newValue = "3,4,5" }));

// Now wait for the editor's own edit to come back to us.
deadline = DateTime.UtcNow.AddSeconds(90);
while (DateTime.UtcNow < deadline)
{
    var message = await ReceiveAsync(timeout.Token);
    if (message == null)
    {
        break;
    }

    if (message.type == MessageTypes.Ping)
    {
        await AnswerPingAsync(message);
        continue;
    }

    if (message.type != MessageTypes.Op)
    {
        continue;
    }

    Log("received_op",
        ("from", message.displayName),
        ("kind", message.opKind),
        ("propertyPath", message.propertyPath),
        ("oldValue", message.oldValue),
        ("newValue", message.newValue),
        ("revision", message.revision));

    File.AppendAllText(Path.Combine(dir, "peer-ops.jsonl"),
        JsonSerializer.Serialize(new
        {
            from = message.displayName,
            opKind = message.opKind,
            propertyPath = message.propertyPath,
            oldValue = message.oldValue,
            newValue = message.newValue,
            revision = message.revision,
        }) + Environment.NewLine);

    if (message.propertyPath == "m_LocalPosition" && message.newValue == expectedFromUnity)
    {
        File.WriteAllText(Path.Combine(dir, "peer-observed.json"),
            JsonSerializer.Serialize(new { newValue = message.newValue, from = message.displayName }));
        Log("observed_unity_edit", ("newValue", message.newValue));
        if (!soak)
        {
            return 0;
        }

        // Stay put: the editor is about to hold the socket open across several probes, and the only
        // way to know that worked from this side is to be here when its next edit arrives.
        deadline = DateTime.UtcNow.AddSeconds(180);
        continue;
    }

    if (soak && message.propertyPath == "m_LocalPosition" && message.newValue == expectedSoak)
    {
        File.WriteAllText(Path.Combine(dir, "peer-soak.json"),
            JsonSerializer.Serialize(new { newValue = message.newValue, from = message.displayName }));
        Log("observed_unity_edit_after_soak", ("newValue", message.newValue));
        return 0;
    }
}

Log(soak ? "timeout_waiting_for_unity_edit_after_soak" : "timeout_waiting_for_unity_edit");
return 1;

// The server probes every 20 s and hangs up after a minute of silence, so a test peer has to answer
// exactly like an editor does.
async Task AnswerPingAsync(WireMessage message)
{
    if (message.type == MessageTypes.Ping)
    {
        await SendAsync(new WireMessage { type = MessageTypes.Pong });
        Log("pong");
    }
}

async Task SendAsync(WireMessage message)
{
    var json = JsonSerializer.Serialize(message, WireJson.Options);
    await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token);
}

async Task<WireMessage> ReadUntilAsync(string type)
{
    while (true)
    {
        var message = await ReceiveAsync(timeout.Token);
        if (message == null)
        {
            throw new InvalidOperationException("socket closed while waiting for " + type);
        }

        if (message.type == MessageTypes.Error)
        {
            Log("server_error", ("code", message.code), ("message", message.message));
            throw new InvalidOperationException("server refused: " + message.code + " " + message.message);
        }

        if (message.type == MessageTypes.Ping)
        {
            await AnswerPingAsync(message);
            continue;
        }

        if (message.type == type)
        {
            return message;
        }
    }
}

async Task<WireMessage?> ReceiveAsync(CancellationToken ct)
{
    var buffer = new byte[16 * 1024];
    using var stream = new MemoryStream();
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, ct);
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

    var json = Encoding.UTF8.GetString(stream.ToArray());
    return JsonSerializer.Deserialize<WireMessage>(json, WireJson.Options);
}

static async Task<string> ReadAllTextAsync(string path)
{
    // A file being written by another process can be briefly locked; retry instead of failing the run.
    for (var attempt = 0; attempt < 20; attempt++)
    {
        try
        {
            return await File.ReadAllTextAsync(path);
        }
        catch (IOException)
        {
            await Task.Delay(50);
        }
    }

    return await File.ReadAllTextAsync(path);
}

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

static class WireJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null,
        // WireMessage is deliberately a bag of public fields (no properties, no inheritance) so that
        // Unity's JsonUtility can read it. System.Text.Json ignores fields unless told otherwise — the
        // first attempt serialized "{}" and the server answered "missing type".
        IncludeFields = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };
}
