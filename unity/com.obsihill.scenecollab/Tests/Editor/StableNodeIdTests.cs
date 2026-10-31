using NUnit.Framework;
using SceneCollab.Domain;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// The id rules are the one thing that cannot be "fixed later": a node id that changes
    /// between two machines silently turns every edit into an edit of a different object.
    /// </summary>
    public sealed class StableNodeIdTests
    {
        const string SampleId = "GlobalObjectId_V1-2-0f5b0a9e0a1c4f0c9f0c1d2e3a4b5c6d-1234567-0";

        [Test]
        public void HashIsDeterministic()
        {
            Assert.AreEqual(
                StableNodeId.FromGlobalObjectId(SampleId),
                StableNodeId.FromGlobalObjectId(SampleId));
        }

        [Test]
        public void HashIgnoresSurroundingWhitespace()
        {
            Assert.AreEqual(
                StableNodeId.FromGlobalObjectId(SampleId),
                StableNodeId.FromGlobalObjectId("  " + SampleId + "\n"));
        }

        [Test]
        public void HashDiffersForDifferentObjects()
        {
            Assert.AreNotEqual(
                StableNodeId.FromGlobalObjectId(SampleId),
                StableNodeId.FromGlobalObjectId(SampleId.Replace("-1234567-", "-7654321-")));
        }

        [Test]
        public void HashIsSixteenLowercaseHexCharacters()
        {
            var id = StableNodeId.FromGlobalObjectId(SampleId);

            Assert.AreEqual(16, id.Length);
            Assert.IsTrue(StableNodeId.IsValid(id), id);
        }

        [Test]
        public void EmptyInputIsRejected()
        {
            Assert.Throws<System.ArgumentException>(() => StableNodeId.FromGlobalObjectId(" "));
            Assert.Throws<System.ArgumentException>(() => StableNodeId.FromGlobalObjectId(null));
        }

        [TestCase("")]
        [TestCase("ABC")]
        [TestCase("12345 g")]
        [TestCase("repair:")]
        public void MalformedIdsAreRejected(string candidate)
        {
            Assert.IsFalse(StableNodeId.IsValid(candidate));
        }

        [Test]
        public void RepairedIdsAreAccepted()
        {
            Assert.IsTrue(StableNodeId.IsValid(StableNodeId.RepairedPrefix + "Main Camera"));
        }

        [Test]
        public void OverlongIdsAreRejected()
        {
            Assert.IsFalse(StableNodeId.IsValid(new string('a', StableNodeId.MaxLength + 1)));
        }
    }
}
