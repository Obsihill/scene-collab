using System.Collections.Generic;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using SceneCollab.Editor.SceneSync;
using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    public sealed class SyncOptimizationTests
    {
        static SceneOperation Position(string oldValue, string newValue, string node = "node")
        {
            return new SceneOperation { Kind = OpKinds.SetProperty, NodeId = node,
                PropertyPath = ScenePropertyAccess.LocalPosition, OldValue = oldValue, NewValue = newValue };
        }

        [Test]
        public void PendingPositionsCoalesceWithoutLosingTheOriginalValue()
        {
            var outbox = new SceneOperationOutbox();
            outbox.Enqueue(Position("0", "1"));
            outbox.Enqueue(Position("1", "2"));
            Assert.AreEqual(1, outbox.Count);
            var combined = outbox.Dequeue();
            Assert.AreEqual("0", combined.OldValue);
            Assert.AreEqual("2", combined.NewValue);
        }

        [Test]
        public void OutboxDoesNotMergeAcrossBarriersOrDiscontinuousValues()
        {
            var outbox = new SceneOperationOutbox();
            outbox.Enqueue(Position("0", "1"));
            outbox.Enqueue(new SceneOperation { Kind = OpKinds.Reparent, NodeId = "node" });
            outbox.Enqueue(Position("1", "2"));
            outbox.Enqueue(Position("other", "3"));
            Assert.AreEqual(4, outbox.Count);
            Assert.AreEqual("1", outbox.Dequeue().NewValue);
            Assert.AreEqual(OpKinds.Reparent, outbox.Dequeue().Kind);
            Assert.AreEqual("2", outbox.Dequeue().NewValue);
            Assert.AreEqual("3", outbox.Dequeue().NewValue);
        }

        [Test]
        public void ARevertedPendingEditProducesNoNetworkOperation()
        {
            var outbox = new SceneOperationOutbox();
            outbox.Enqueue(Position("0", "1"));
            outbox.Enqueue(Position("1", "0"));
            Assert.AreEqual(0, outbox.Count);
        }

        [Test]
        public void RemotePositionMovesSmoothlyAndFinishesExactly()
        {
            var go = new GameObject("remote");
            try
            {
                var interpolation = new RemotePositionInterpolation();
                go.transform.localPosition = new Vector3(10, 0, 0);
                interpolation.Start("node", go.transform, Vector3.zero, 0);
                interpolation.Tick(0.05);
                Assert.AreEqual(5f, go.transform.localPosition.x, 0.001f);
                interpolation.Tick(0.1);
                Assert.AreEqual(10f, go.transform.localPosition.x, 0.001f);
                Assert.IsFalse(interpolation.IsAnimating("node"));
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void LocalEditCancelsInterpolationAndIsNotOverwrittenOnFinish()
        {
            var go = new GameObject("local");
            try
            {
                var interpolation = new RemotePositionInterpolation();
                go.transform.localPosition = Vector3.right * 10;
                interpolation.Start("node", go.transform, Vector3.zero, 0);
                interpolation.Tick(0.05);
                go.transform.localPosition = Vector3.up * 3;
                Assert.IsFalse(interpolation.IsAnimating("node"));
                interpolation.Finish();
                Assert.AreEqual(Vector3.up * 3, go.transform.localPosition);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        public void FinishUsesAuthoritativeValueAndInterpolatedPositionDoesNotEcho()
        {
            var go = new GameObject("remote");
            try
            {
                var diff = new SceneDiff();
                diff.Baseline(go, "node");
                var interpolation = new RemotePositionInterpolation();
                go.transform.localPosition = Vector3.right * 10;
                diff.NoteRemoteValue("node", ScenePropertyAccess.LocalPosition, "10,0,0");
                interpolation.Start("node", go.transform, Vector3.zero, 0);
                interpolation.Tick(0.05);
                var operations = new List<SceneOperation>();
                diff.CollectInto(go, "node", operations, interpolation.IsAnimating("node"));
                Assert.IsEmpty(operations);
                interpolation.Finish();
                Assert.AreEqual(Vector3.right * 10, go.transform.localPosition);
                diff.CollectInto(go, "node", operations);
                Assert.IsEmpty(operations);
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
