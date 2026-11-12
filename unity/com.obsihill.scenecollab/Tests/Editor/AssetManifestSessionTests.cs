using System.Reflection;
using NUnit.Framework;
using SceneCollab.Editor;

namespace SceneCollab.Tests.Editor
{
    public class AssetManifestSessionTests
    {
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
