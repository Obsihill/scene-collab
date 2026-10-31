using System.Collections.Generic;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Editor.SceneSync;
using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// Guards the two halves of "a new object reaches the other editor": the hierarchy-changed flag
    /// surviving the drain that clears the dirty set, and the components following the add op.
    /// </summary>
    public class StructureEmissionTests
    {
        [Test]
        public void DrainHandsTheStructureFlagToTheCaller()
        {
            var feed = new SceneChangeFeed();
            feed.NoteStructureChanged();

            var dirty = new List<GameObject>();
            Assert.IsTrue(feed.Drain(dirty), "a structural edit must reach the sweep that turns it into ops");
            Assert.IsFalse(feed.Drain(dirty), "the flag is consumed once, not left set forever");
        }

        [Test]
        public void NewObjectSendsItsComponentsAfterTheAdd()
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                var registry = new SceneNodeRegistry();
                var structure = new SceneStructure(registry);
                var nodeId = SceneStructure.NewNodeId();

                bool created;
                var add = structure.BuildAdd(cube, nodeId, out created);

                Assert.IsTrue(created, "a root object must be addable");
                Assert.AreEqual(OpKinds.AddNode, add.Kind);

                var components = structure.CollectComponentChanges(cube, nodeId);
                Assert.Greater(
                    components.Count,
                    0,
                    "mesh / collider components have to follow the add, or the peer gets a bare Transform");

                for (var i = 0; i < components.Count; i++)
                {
                    Assert.AreEqual(OpKinds.AddComponent, components[i].Kind);
                    Assert.AreNotEqual(
                        typeof(Transform).FullName,
                        components[i].ComponentType,
                        "a Transform cannot be added to a GameObject");
                }

                // A second sweep must be quiet: the signature was consumed.
                Assert.AreEqual(0, structure.CollectComponentChanges(cube, nodeId).Count);
            }
            finally
            {
                Object.DestroyImmediate(cube);
            }
        }
    }
}
