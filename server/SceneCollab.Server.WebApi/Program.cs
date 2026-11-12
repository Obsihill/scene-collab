using System;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Server.WebApi.WebSockets;

// ─────────────────────────────────────────────────────────────────────────────
// Scene Collab server.
//
// Fail-closed by design: without an access token the process refuses to start, and it
// binds to loopback unless told otherwise. An accidentally exposed room is a full
// scene leak, so the unsafe path has to be typed out explicitly (--insecure-dev).
//
// Routing is raw middleware rather than minimal-API Map/MapGet: the endpoint builder
// overloads are ambiguous between RequestDelegate and Delegate, and the resulting error
// messages cost more than the three lines saved.
// ─────────────────────────────────────────────────────────────────────────────

var startedUtc = DateTime.UtcNow;
var serverVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

// `--health-check` lets a container runtime ask this build whether the process is actually serving.
// The runtime image ships no curl or bash, and probing with `dotnet --info` only proves the .NET host
// exists, which turns an unhealthy server into a green check.
if (args.Contains("--health-check"))
{
    return await ProbeSelfAsync();
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SessionHub>();

var bindAddress = Environment.GetEnvironmentVariable("SCENECOLLAB_BIND");
if (string.IsNullOrWhiteSpace(bindAddress))
{
    bindAddress = "127.0.0.1";
}

var port = Environment.GetEnvironmentVariable("SCENECOLLAB_PORT");
if (string.IsNullOrWhiteSpace(port))
{
    port = "8080";
}

builder.WebHost.UseUrls("http://" + bindAddress + ":" + port);

var accessToken = Environment.GetEnvironmentVariable("SCENECOLLAB_TOKEN");
if (string.IsNullOrWhiteSpace(accessToken))
{
    accessToken = builder.Configuration["SceneCollab:Token"];
}

var insecureDev = args.Any(a => string.Equals(a, "--insecure-dev", StringComparison.OrdinalIgnoreCase))
    || string.Equals(Environment.GetEnvironmentVariable("SCENECOLLAB_INSECURE_DEV"), "1", StringComparison.Ordinal);

var app = builder.Build();
var log = app.Logger;

if (string.IsNullOrWhiteSpace(accessToken))
{
    if (!insecureDev)
    {
        log.LogCritical("SCENECOLLAB_TOKEN is not set. Refusing to start (use --insecure-dev for local tests only).");
        return 2;
    }

    log.LogWarning("Authentication DISABLED (--insecure-dev). Anyone who can reach {Bind}:{Port} can read and edit scenes.",
        bindAddress, port);
}

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });

app.Run(async context =>
{
    var path = context.Request.Path.Value ?? "/";

    if (string.Equals(path, "/ws", StringComparison.OrdinalIgnoreCase))
    {
        await HandleWebSocketAsync(context);
        return;
    }

    if (string.Equals(path, "/health", StringComparison.OrdinalIgnoreCase))
    {
        await WriteHealthAsync(context);
        return;
    }

    if (string.Equals(path, "/rooms", StringComparison.OrdinalIgnoreCase))
    {
        await WriteRoomsAsync(context);
        return;
    }

    if (path == "/" || path.Length == 0)
    {
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            "Scene Collab server " + serverVersion + "\nprotocol " + ProtocolVersion.Current
            + "\nGET /health\nGET /rooms\nWS  /ws?room=<room>  (header X-SceneCollab-Token)\n");
        return;
    }

    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsync("not found");
});

log.LogInformation("Scene Collab {Version} listening on http://{Bind}:{Port} (auth: {Auth})",
    serverVersion, bindAddress, port, string.IsNullOrWhiteSpace(accessToken) ? "disabled" : "token");

app.Run();
return 0;

// ─────────────────────────────────────────────────────────────────────────────

async Task HandleWebSocketAsync(HttpContext context)
{
    var hub = context.RequestServices.GetRequiredService<SessionHub>();

    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("websocket upgrade required");
        return;
    }

    var roomKey = context.Request.Query["room"].ToString();
    if (string.IsNullOrWhiteSpace(roomKey))
    {
        roomKey = "default";
    }

    if (!Regex.IsMatch(roomKey, "^[A-Za-z0-9._-]{1,64}$"))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("invalid room name");
        return;
    }

    if (!string.IsNullOrWhiteSpace(accessToken))
    {
        var provided = context.Request.Headers["X-SceneCollab-Token"].ToString();
        if (string.IsNullOrEmpty(provided))
        {
            // Convenience for curl/wscat. Note: a token in a query string ends up in
            // proxy access logs — the editor always uses the header.
            provided = context.Request.Query["token"].ToString();
        }

        if (!FixedTimeEquals(provided, accessToken))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var connection = new ClientConnection(socket, NewClientId(), roomKey);
    var room = hub.GetRoom(roomKey);

    log.LogInformation("connect room={Room} client={Client} peers={Peers}",
        roomKey, connection.ClientId, room.ConnectionCount);

    try
    {
        await room.HandleAsync(socket, connection, context.RequestAborted);
    }
    finally
    {
        hub.ReleaseRoom(room);
    }

    log.LogInformation("disconnect room={Room} client={Client} peers={Peers}",
        roomKey, connection.ClientId, room.ConnectionCount);
}

async Task WriteRoomsAsync(HttpContext context)
{
    if (!IsAuthorized(context))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }

    var hub = context.RequestServices.GetRequiredService<SessionHub>();

    // Rooms with no sockets are shells left behind by a finished session; listing them would show
    // scenes nobody is editing and invite clients into a room with no host.
    var rooms = hub.SnapshotRooms()
        .Where(room => room.ConnectionCount > 0)
        .OrderBy(room => room.Key, StringComparer.Ordinal)
        .Select(room => new
        {
            room = room.Key,
            sceneName = room.SceneName,
            sceneGuid = room.SceneGuid,
            host = room.HostDisplayName,
            participants = room.ConnectionCount,
            locks = room.LockCount,
        })
        .ToArray();

    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsync(JsonSerializer.Serialize(new { rooms }));
}

bool IsAuthorized(HttpContext context)
{
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return true;
    }

    var provided = context.Request.Headers["X-SceneCollab-Token"].ToString();
    if (string.IsNullOrEmpty(provided))
    {
        provided = context.Request.Query["token"].ToString();
    }

    return FixedTimeEquals(provided, accessToken);
}

async Task WriteHealthAsync(HttpContext context)
{
    var hub = context.RequestServices.GetRequiredService<SessionHub>();

    var payload = new
    {
        status = "ok",
        protocolVersion = ProtocolVersion.Current,
        minProtocolVersion = ProtocolVersion.MinCompatible,
        serverVersion,
        authRequired = !string.IsNullOrWhiteSpace(accessToken),
        rooms = hub.RoomCount,
        uptimeSeconds = (long)(DateTime.UtcNow - startedUtc).TotalSeconds,
    };

    context.Response.ContentType = "application/json; charset=utf-8";
    await context.Response.WriteAsync(JsonSerializer.Serialize(payload));
}

static bool FixedTimeEquals(string provided, string expected)
{
    if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected))
    {
        return false;
    }

    var a = Encoding.UTF8.GetBytes(provided);
    var b = Encoding.UTF8.GetBytes(expected);

    // Length itself is not secret; compare in constant time only when lengths match.
    return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
}

static string NewClientId()
{
    return Guid.NewGuid().ToString("N").Substring(0, 10);
}

static async Task<int> ProbeSelfAsync()
{
    var port = Environment.GetEnvironmentVariable("SCENECOLLAB_PORT");
    if (string.IsNullOrWhiteSpace(port))
    {
        port = "8080";
    }

    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        using var response = await client.GetAsync("http://127.0.0.1:" + port + "/health");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception)
    {
        return 1;
    }
}
