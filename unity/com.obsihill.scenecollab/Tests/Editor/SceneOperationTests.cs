using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using SceneCollab.Editor;

namespace SceneCollab.Editor.Tests
{
    public sealed class SceneOperationTests
    {
        const string Node = "0123456789abcdef";

        [Test]
        public void PropertyOpWithOldValueIsAccepted()
        {
            SceneOperation operation;
            string error;

            Assert.IsTrue(SceneOperation.TryCreate(
                OpKinds.SetProperty, Node, "m_LocalPosition.x", "0", "1.5", out operation, out error));
            Assert.IsNull(error);
            Assert.AreEqual("1.5", operation.NewValue);
        }

        [Test]
        public void PropertyOpWithoutOldValueIsRejected()
        {
            SceneOperation operation;
            string error;

            // Without the previous value the server cannot tell a fresh edit from a stale one.
            Assert.IsFalse(SceneOperation.TryCreate(
                OpKinds.SetProperty, Node, "m_LocalPosition.x", null, "1.5", out operation, out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void AddNodeDoesNotRequireOldValue()
        {
            SceneOperation operation;
            string error;

            Assert.IsTrue(SceneOperation.TryCreate(
                OpKinds.AddNode, Node, null, null, "{}", out operation, out error));
        }

        [Test]
        public void MalformedNodeIdIsRejected()
        {
            SceneOperation operation;
            string error;

            Assert.IsFalse(SceneOperation.TryCreate(
                OpKinds.SetProperty, "not-a-hash", "m_Name", "a", "b", out operation, out error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void SetPropertyRequiresPropertyPath()
        {
            SceneOperation operation;
            string error;

            Assert.IsFalse(SceneOperation.TryCreate(
                OpKinds.SetProperty, Node, "", "a", "b", out operation, out error));
        }

        [Test]
        public void WireMessageCarriesRevisionAndIdempotencyKey()
        {
            SceneOperation operation;
            string error;
            SceneOperation.TryCreate(OpKinds.SetProperty, Node, "m_Name", "old", "new", out operation, out error);

            var wire = operation.ToWire("client-1", "room-1", 7, "op-1");

            Assert.AreEqual(MessageTypes.Op, wire.type);
            Assert.AreEqual(7, wire.revision);
            Assert.AreEqual("op-1", wire.opId);
            Assert.AreEqual(Node, wire.nodeId);
        }
    }

    public sealed class CollabSettingsTests
    {
        [TestCase("http://host:8080", "ws://host:8080/ws")]
        [TestCase("localhost:8080", "ws://localhost:8080/ws")]
        [TestCase("wss://collab.example.com", "wss://collab.example.com/ws")]
        [TestCase("ws://127.0.0.1:8080/ws", "ws://127.0.0.1:8080/ws")]
        public void AddressesAreNormalised(string input, string expected)
        {
            Assert.AreEqual(expected, CollabSettings.Normalize(input));
        }

        [Test]
        public void EmptyAddressFallsBackToLoopback()
        {
            Assert.AreEqual("ws://127.0.0.1:8080/ws", CollabSettings.Normalize(null));
        }

        [Test]
        public void RoomIsAddedToTheQueryString()
        {
            var settings = new CollabSettings { serverUrl = "ws://host:8080/ws", room = "team a" };

            StringAssert.Contains("room=team%20a", settings.BuildConnectionUrl());
        }

        [Test]
        public void RoomIsAddedAfterAnExistingQuery()
        {
            var settings = new CollabSettings { serverUrl = "ws://host:8080/ws?x=1", room = "abc" };

            StringAssert.Contains("?x=1&room=abc", settings.BuildConnectionUrl());
        }
    }
}
