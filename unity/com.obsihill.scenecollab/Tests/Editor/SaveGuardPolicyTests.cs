using System.IO;
using NUnit.Framework;
using SceneCollab.Editor.SceneSync;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// Who may write the scene file, and what the guard does about the writes it cannot veto.
    /// </summary>
    public sealed class SaveGuardPolicyTests
    {
        const string Scene = "Assets/Scenes/Main.unity";
        const string Material = "Assets/Materials/Red.mat";

        [Test]
        public void AConnectedGuestIsBlockedFromSavingTheScene()
        {
            var decision = SaveGuardPolicy.Decide(true, true, false, true, Scene);

            Assert.AreEqual(SaveGuardDecision.BlockGuest, decision);
            StringAssert.Contains("only the host", SaveGuardPolicy.Describe(decision, Scene));
        }

        [Test]
        public void AVerifiedHostMaySave()
        {
            Assert.AreEqual(SaveGuardDecision.Allow, SaveGuardPolicy.Decide(true, true, true, true, Scene));
        }

        [Test]
        public void APromotedHostStillCatchingUpIsHeld()
        {
            var decision = SaveGuardPolicy.Decide(true, true, true, false, Scene);

            Assert.AreEqual(SaveGuardDecision.BlockHostVerifying, decision);
            StringAssert.Contains("still catching up", SaveGuardPolicy.Describe(decision, Scene));
        }

        [Test]
        public void NonSceneAssetsAreNeverBlocked()
        {
            Assert.AreEqual(
                SaveGuardDecision.AllowNotScene,
                SaveGuardPolicy.Decide(true, true, false, true, Material));
        }

        [Test]
        public void NothingIsBlockedWhenSyncIsOff()
        {
            Assert.AreEqual(SaveGuardDecision.Allow, SaveGuardPolicy.Decide(false, true, false, false, Scene));
        }

        [Test]
        public void OnlyUnityScenePathsArePoliced()
        {
            Assert.IsTrue(SceneSaveGuard.IsScenePath(Scene));
            Assert.IsTrue(SceneSaveGuard.IsScenePath("C:/project/Assets/x.UNITY"));
            Assert.IsFalse(SceneSaveGuard.IsScenePath(Material));
            Assert.IsFalse(SceneSaveGuard.IsScenePath(null));
            Assert.IsFalse(SceneSaveGuard.IsScenePath(string.Empty));
            Assert.IsFalse(SceneSaveGuard.IsScenePath("Assets/Scenes/Main.unity.bak"));
        }

        [Test]
        public void TheBypassDetectorHashesContentNotTimestamps()
        {
            var path = Path.Combine(Path.GetTempPath(), "scenecollab-guard-" + Path.GetRandomFileName() + ".unity");

            File.WriteAllText(path, "%YAML 1.1\n--- !u!1 &1\n");
            var first = SceneSaveGuard.HashOf(path);

            File.WriteAllText(path, "%YAML 1.1\n--- !u!1 &1\n");
            var same = SceneSaveGuard.HashOf(path);

            File.WriteAllText(path, "%YAML 1.1\n--- !u!1 &2\n");
            var changed = SceneSaveGuard.HashOf(path);

            File.Delete(path);

            Assert.IsNotEmpty(first);
            Assert.AreEqual(first, same);
            Assert.AreNotEqual(first, changed);
        }

        [Test]
        public void AMissingFileHashesToEmptyRatherThanThrowing()
        {
            Assert.AreEqual(string.Empty, SceneSaveGuard.HashOf("C:/does/not/exist.unity"));
            Assert.AreEqual(string.Empty, SceneSaveGuard.HashOf(null));
        }
    }
}
