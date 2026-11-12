using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Server.WebApi.WebSockets;
using Xunit;

namespace SceneCollab.Domain.Tests;

public class RoomConcurrencyTests
{
    [Fact]
    public void PendingConnectionKeepsRoomAliveAcrossLastDisconnect()
    {
        var hub = new SessionHub();
        var first = hub.GetRoom("test");
        var pending = hub.GetRoom("test");
        hub.ReleaseRoom(first);
        var next = hub.GetRoom("test");
        Assert.Same(pending, next);
        hub.ReleaseRoom(pending);
        hub.ReleaseRoom(next);
        Assert.Equal(0, hub.RoomCount);
        Assert.NotSame(first, hub.GetRoom("test"));
    }

    [Fact]
    public async Task LaterOperationWaitsUntilEarlierOperationIsDelivered()
    {
        var room = new RoomSession("test");
        var slow = new TestSocket(true);
        var other = new TestSocket(false);
        var first = new ClientConnection(slow, "first", "test");
        var second = new ClientConnection(other, "second", "test");
        room.State.Join("first", "first", null);
        room.State.Join("second", "second", null);
        var clients = (Dictionary<string, ClientConnection>)typeof(RoomSession)
            .GetField("_clients", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(room);
        clients.Add("first", first);
        clients.Add("second", second);
        var method = typeof(RoomSession).GetMethod("HandleOpAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Task Send(ClientConnection client, string id, long revision) => (Task)method.Invoke(room, new object[] {
            client, new WireMessage { opId = id, opKind = OpKinds.SetProperty, revision = revision }, CancellationToken.None });
        var one = Send(first, "one", 0);
        await slow.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var two = Send(second, "two", 1);
        Assert.Equal(1, room.State.Revision);
        slow.Release.TrySetResult(true);
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, room.State.Revision);
        Assert.Contains(other.Messages, message => message.Contains("\"opId\":\"one\""));
    }

    sealed class TestSocket : WebSocket
    {
        readonly bool _block;
        public readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<string> Messages = new();
        public TestSocket(bool block) { _block = block; }
        public override WebSocketState State => WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string CloseStatusDescription => null;
        public override string SubProtocol => null;
        public override void Abort() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) => Task.CompletedTask;
        public override void Dispose() { }
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
        {
            if (_block && !Started.Task.IsCompleted) { Started.TrySetResult(true); await Release.Task; }
            Messages.Add(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count));
        }
    }
}
