using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Smoke
{
    /// <summary>
    /// Two-client end-to-end check against a running server:
    /// A joins -> B joins -> A sends an op -> A gets ack(accepted) -> B receives that op.
    ///
    ///   dotnet run --project tools/SceneCollab.Smoke -- --url ws://127.0.0.1:8080/ws --token dev
    ///
    /// Exit code 0 = the collaboration loop works. Anything else = it does not.
    /// </summary>
    internal static class Program
    {
        internal static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        static async Task<int> Main(string[] args)
        {
            var url = GetArg(args, "--url") ?? "ws://127.0.0.1:8080/ws";
            var token = GetArg(args, "--token") ?? string.Empty;
            var room = GetArg(args, "--room") ?? "smoke";

            Console.WriteLine("server  : " + url);
            Console.WriteLine("room    : " + room);

            try
            {
                using var alice = await Socket.ConnectAsync(url, token, room);
                var welcomeA = await alice.HandshakeAsync("Alice", "alice");
                Console.WriteLine("alice   : clientId=" + welcomeA.clientId
                    + " scribe=" + welcomeA.isScribe + " revision=" + welcomeA.revision);

                if (!welcomeA.isScribe)
                {
                    Console.WriteLine("FAIL: first client in a room must be the scribe");
                    return 3;
                }

                using var bob = await Socket.ConnectAsync(url, token, room);
                var welcomeB = await bob.HandshakeAsync("Bob", "bob");
                Console.WriteLine("bob     : clientId=" + welcomeB.clientId
                    + " scribe=" + welcomeB.isScribe + " revision=" + welcomeB.revision);

                if (welcomeB.isScribe)
                {
                    Console.WriteLine("FAIL: second client must not be the scribe");
                    return 4;
                }

                // Alice must observe Bob joining.
                var presence = await alice.ReceiveAsync(TimeSpan.FromSeconds(5));
                if (presence == null || presence.type != MessageTypes.Presence || presence.participants == null
                    || presence.participants.Length != 2)
                {
                    Console.WriteLine("FAIL: expected presence with 2 participants, got "
                        + (presence == null ? "nothing" : presence.type));
                    return 5;
                }

                Console.WriteLine("presence: " + presence.participants.Length + " participants");

                var opId = Guid.NewGuid().ToString("N");
                await alice.SendAsync(new WireMessage
                {
                    type = MessageTypes.Op,
                    protocolVersion = ProtocolVersion.Current,
                    opId = opId,
                    opKind = OpKinds.SetProperty,
                    nodeId = "e2e-node",
                    propertyPath = "m_LocalPosition.x",
                    oldValue = "0",
                    newValue = "1",
                    revision = welcomeA.revision,
                });

                var ack = await alice.ReceiveAsync(TimeSpan.FromSeconds(5));
                if (ack == null || ack.type != MessageTypes.Ack || !ack.accepted || ack.opId != opId)
                {
                    Console.WriteLine("FAIL: expected accepted ack, got " + Describe(ack));
                    return 6;
                }

                Console.WriteLine("ack     : accepted revision=" + ack.revision);

                var relayed = await bob.ReceiveAsync(TimeSpan.FromSeconds(5));
                if (relayed == null || relayed.type != MessageTypes.Op || relayed.opId != opId
                    || relayed.newValue != "1" || relayed.revision != ack.revision)
                {
                    Console.WriteLine("FAIL: op was not relayed to the peer, got " + Describe(relayed));
                    return 7;
                }

                Console.WriteLine("relay   : bob received op at revision=" + relayed.revision);

                // A duplicate send must be idempotent, not a second applied op.
                await alice.SendAsync(new WireMessage
                {
                    type = MessageTypes.Op,
                    protocolVersion = ProtocolVersion.Current,
                    opId = opId,
                    opKind = OpKinds.SetProperty,
                    nodeId = "e2e-node",
                    propertyPath = "m_LocalPosition.x",
                    oldValue = "0",
                    newValue = "1",
                    revision = ack.revision,
                });

                var replay = await alice.ReceiveAsync(TimeSpan.FromSeconds(5));
                if (replay == null || replay.type != MessageTypes.Ack || !replay.duplicate || replay.revision != ack.revision)
                {
                    Console.WriteLine("FAIL: replay was not idempotent, got " + Describe(replay));
                    return 8;
                }

                Console.WriteLine("replay  : duplicate ack, revision unchanged");

                // Wrong token must be refused before the upgrade completes.
                try
                {
                    using var intruder = await Socket.ConnectAsync(url, token + "-wrong", room);
                    Console.WriteLine("FAIL: connection with a wrong token was accepted");
                    return 9;
                }
                catch (WebSocketException)
                {
                    Console.WriteLine("auth    : wrong token rejected");
                }

                Console.WriteLine("PASS");
                return 0;
            }
            catch (Exception exception)
            {
                Console.WriteLine("FAIL: " + exception.GetType().Name + ": " + exception.Message);
                return 1;
            }
        }

        static string Describe(WireMessage message)
        {
            return message == null
                ? "nothing"
                : message.type + " code=" + (message.code ?? "-") + " message=" + (message.message ?? "-");
        }

        static string GetArg(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }

    internal sealed class Socket : IDisposable
    {
        readonly ClientWebSocket _socket = new ClientWebSocket();

        Socket()
        {
        }

        public static async Task<Socket> ConnectAsync(string url, string token, string room)
        {
            var socket = new Socket();
            if (!string.IsNullOrEmpty(token))
            {
                socket._socket.Options.SetRequestHeader("X-SceneCollab-Token", token);
            }

            var separator = url.Contains("?") ? "&" : "?";
            await socket._socket.ConnectAsync(new Uri(url + separator + "room=" + room), CancellationToken.None);
            return socket;
        }

        public async Task<WireMessage> HandshakeAsync(string displayName, string color)
        {
            await SendAsync(new WireMessage
            {
                type = MessageTypes.Hello,
                protocolVersion = ProtocolVersion.Current,
                displayName = displayName,
                color = color,
            });

            var welcome = await ReceiveAsync(TimeSpan.FromSeconds(5));
            if (welcome == null || welcome.type != MessageTypes.Welcome)
            {
                throw new InvalidOperationException("expected welcome, got "
                    + (welcome == null ? "nothing" : welcome.type));
            }

            return welcome;
        }

        public Task SendAsync(WireMessage message)
        {
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Program.Json));
            return _socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task<WireMessage> ReceiveAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            var buffer = new byte[16384];
            var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            return JsonSerializer.Deserialize<WireMessage>(json, Program.Json);
        }

        public void Dispose()
        {
            _socket.Dispose();
        }
    }
}
