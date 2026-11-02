using NUnit.Framework;
using SceneCollab.Editor.SceneSync;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// The rule that keeps Ctrl+Z from reaching into somebody else's work.
    ///
    /// These are plain dictionary tests on purpose: the interesting part of undo consistency is the
    /// ownership decision, and it is the part that has to be provably right before an editor is in the
    /// loop.
    /// </summary>
    public sealed class UndoArbitrationTests
    {
        [Test]
        public void AnUndoOfAFieldThisEditorWroteLastIsPublished()
        {
            var undo = new UndoArbitration { SelfId = "A" };
            undo.NoteWrite("n1", "m_LocalPosition.x", "A");

            Assert.IsTrue(undo.ShouldPublish("n1", "m_LocalPosition.x"));
            Assert.AreEqual(1, undo.Allowed);
            Assert.AreEqual(0, undo.Refused);
        }

        [Test]
        public void AnUndoAfterAPeerWroteTheSameFieldIsRefused()
        {
            var undo = new UndoArbitration { SelfId = "A" };
            undo.NoteWrite("n1", "m_LocalPosition.x", "A");
            undo.NoteWrite("n1", "m_LocalPosition.x", "B");

            Assert.IsFalse(undo.ShouldPublish("n1", "m_LocalPosition.x"));
            Assert.AreEqual(1, undo.Refused);
            Assert.AreEqual("B", undo.OwnerOf("n1", "m_LocalPosition.x"));
        }

        [Test]
        public void APeerWritingOneFieldDoesNotBlockTheOthers()
        {
            var undo = new UndoArbitration { SelfId = "A" };
            undo.NoteWrite("n1", "m_LocalPosition.x", "A");
            undo.NoteWrite("n1", "m_LocalPosition.y", "A");
            undo.NoteWrite("n1", "m_LocalPosition.y", "B");

            Assert.IsTrue(undo.ShouldPublish("n1", "m_LocalPosition.x"));
            Assert.IsFalse(undo.ShouldPublish("n1", "m_LocalPosition.y"));
            Assert.AreEqual(1, undo.Allowed);
            Assert.AreEqual(1, undo.Refused);
        }

        [Test]
        public void AFieldNobodyClaimsIsAllowedSoUndoWorksBeforeTheFirstRoundTrip()
        {
            var undo = new UndoArbitration { SelfId = "A" };

            Assert.IsTrue(undo.ShouldPublish("n1", "m_IsActive"));
            Assert.IsNull(undo.OwnerOf("n1", "m_IsActive"));
        }

        [Test]
        public void AnUnknownSelfIdAllowsEverything()
        {
            // Before the welcome frame there is no client id. Refusing then would make undo silently
            // dead for the first second of every session, which is worse than the risk it avoids.
            var undo = new UndoArbitration();
            undo.NoteWrite("n1", "m_Name", "B");

            Assert.IsTrue(undo.ShouldPublish("n1", "m_Name"));
        }

        [Test]
        public void DeletingANodeForgetsItsFields()
        {
            var undo = new UndoArbitration { SelfId = "A" };
            undo.NoteWrite("n1", "m_Name", "B");
            undo.NoteWrite("n2", "m_Name", "B");

            undo.Forget("n1");

            Assert.IsNull(undo.OwnerOf("n1", "m_Name"));
            Assert.AreEqual("B", undo.OwnerOf("n2", "m_Name"));
            Assert.AreEqual(1, undo.TrackedFields);
        }

        [Test]
        public void CountersRestartBetweenUndos()
        {
            var undo = new UndoArbitration { SelfId = "A" };
            undo.NoteWrite("n1", "m_Name", "B");

            undo.ShouldPublish("n1", "m_Name");
            undo.ResetCounters();

            Assert.AreEqual(0, undo.Refused);
            Assert.AreEqual(0, undo.Allowed);
            Assert.AreEqual("B", undo.OwnerOf("n1", "m_Name"));
        }

        [Test]
        public void ClearDropsEverything()
        {
            var undo = new UndoArbitration { SelfId = "A" };
            undo.NoteWrite("n1", "m_Name", "B");
            undo.Clear();

            Assert.AreEqual(0, undo.TrackedFields);
            Assert.IsTrue(undo.ShouldPublish("n1", "m_Name"));
        }
    }
}
