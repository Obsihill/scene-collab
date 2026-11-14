using System.Reflection;
using NUnit.Framework;
using SceneCollab.Editor;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Tests.Editor
{
    public class AssetManifestSessionTests
    {
        [Test]
        public void PresencePromotesTheLocalHostWithoutAnotherWelcome()
        {
            using (var session = new CollabSession())
            {
                var handle = typeof(CollabSession).GetMethod("HandleMessage", BindingFlags.Instance | BindingFlags.NonPublic);
                handle.Invoke(session, new object[] { "{\"type\":\"welcome\",\"clientId\":\"self\",\"isHost\":false}" });
                Assert.IsFalse(session.IsHost);
                var changed = 0;
                session.StateChanged += () => changed++;
                handle.Invoke(session, new object[] { "{\"type\":\"presence\",\"participants\":[{\"clientId\":\"self\",\"isHost\":true,\"isScribe\":true}]}" });
                Assert.IsTrue(session.IsHost);
                Assert.IsTrue(session.IsScribe);
                Assert.AreEqual(1, changed);
            }
        }

        [Test]
        public void EmptyManifestClearsPreviousAssets()
        {
            var session = new CollabSession();
            var handle = typeof(CollabSession).GetMethod("HandleMessage", BindingFlags.Instance | BindingFlags.NonPublic);
            handle.Invoke(session, new object[] { "{\"type\":\"assets\",\"assetGuids\":[\"old\"],\"assetCount\":1}" });
            Assert.AreEqual(1, session.AssetGuids.Length);
            handle.Invoke(session, new object[] { "{\"type\":\"assets\",\"assetGuids\":[],\"assetCount\":0}" });
            Assert.AreEqual(0, session.AssetGuids.Length);
            Assert.AreEqual(0, session.AssetCount);
            session.Dispose();
        }
    }
}
