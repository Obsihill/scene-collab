using NUnit.Framework;
using SceneCollab.Editor.SceneSync;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// The gate between "promoted to host" and "allowed to write the scene".
    ///
    /// The failure this prevents is one-way: a promoted host that saves while its catch-up burst is
    /// still draining writes a scene missing the tail of the session, and every editor that opens that
    /// file afterwards inherits the hole.
    /// </summary>
    public sealed class SyncReadinessTests
    {
        const double Quiet = SyncReadiness.QuietSeconds;

        [Test]
        public void AHostThatOpenedTheSceneIsReadyImmediately()
        {
            var gate = new SyncReadiness();

            gate.Tick(0, false);

            Assert.IsTrue(gate.IsReady);
            Assert.AreEqual(0, gate.Verified);
        }

        [Test]
        public void APromotedHostIsNotReadyOnTheFirstFrame()
        {
            var gate = new SyncReadiness();
            gate.MarkPromoted(10);

            gate.Tick(10, false);

            Assert.IsFalse(gate.IsReady);
            Assert.AreEqual("promoted: settling", gate.Reason);
        }

        [Test]
        public void APromotedHostWithWorkInFlightIsNotReady()
        {
            var gate = new SyncReadiness();
            gate.MarkPromoted(10);

            gate.Tick(10 + Quiet * 4, true);

            Assert.IsFalse(gate.IsReady);
            Assert.AreEqual("promoted: pending work", gate.Reason);
        }

        [Test]
        public void APromotedHostBecomesReadyWhenTheRoomGoesQuiet()
        {
            var gate = new SyncReadiness();
            gate.MarkPromoted(10);

            gate.Tick(10 + Quiet + 0.01, false);

            Assert.IsTrue(gate.IsReady);
            Assert.AreEqual(1, gate.Verified);
            Assert.AreEqual("promoted: caught up", gate.Reason);
        }

        [Test]
        public void AnOpArrivingAfterVerificationRestartsTheFaithPeriod()
        {
            var gate = new SyncReadiness();
            gate.MarkPromoted(10);
            gate.Tick(10 + Quiet + 0.01, false);
            Assert.IsTrue(gate.IsReady);

            gate.NoteInbound(20);
            gate.Tick(20, false);

            Assert.IsFalse(gate.IsReady, "a late op means the queue was not actually empty");
            Assert.AreEqual(1, gate.Verified, "verification is counted once per promotion");
        }

        [Test]
        public void ResetClearsTheGateWhenSyncStops()
        {
            var gate = new SyncReadiness();
            gate.MarkPromoted(10);
            gate.Tick(10, false);
            Assert.IsFalse(gate.IsReady);

            gate.Reset();

            Assert.IsTrue(gate.IsReady);
            Assert.AreEqual("ready", gate.Reason);
        }
    }
}
