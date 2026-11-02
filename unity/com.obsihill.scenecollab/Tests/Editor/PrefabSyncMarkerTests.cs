using System.Collections.Generic;
using NUnit.Framework;
using SceneCollab.Contracts.Protocol;
using SceneCollab.Domain;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// v0.5.3: the regression behind "the prefab synchronised fine, but it still shows an ERROR".
    ///
    /// Both halves of the marker system are exercised end to end here against a prefab that really exists:
    /// the applier must not mark a prefab it could instantiate, and the health scan must not mark objects
    /// whose mesh and material are intact. If either produces a marker on healthy content, a guest sees a
    /// magenta ERROR cube sitting on a perfectly ordinary prefab — which is exactly the report this file
    /// pins down.
    /// </summary>
    public sealed class PrefabSyncMarkerTests
    {
        const string TempFolder = "Assets/__collab_tests";

        readonly List<string> _createdAssets = new List<string>();
        int _saved;

        [SetUp]
        public void SetUp()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", "__collab_tests");
            }
        }

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _createdAssets.Count; i++)
            {
                AssetDatabase.DeleteAsset(_createdAssets[i]);
            }

            _createdAssets.Clear();
            CollabPlaceholders.Revalidate();
        }

        /// <summary>A prefab with a real mesh and a real material — the ordinary case that got marked.</summary>
        string CreatePrefabAsset(out GameObject source)
        {
            source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.name = "SyncedPrefab";

            var path = TempFolder + "/Synced_" + (++_saved) + ".prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, path);
            _createdAssets.Add(path);
            Assert.IsNotNull(prefab);
            return path;
        }

        static WireMessage AddNodeOp(string nodeId, string prefabGuid)
        {
            return new WireMessage
            {
                type = MessageTypes.Op,
                opKind = OpKinds.AddNode,
                nodeId = nodeId,
                prefabGuid = prefabGuid,
                parentNodeId = null,
                siblingIndex = 0,
                newValue = "{\"name\":\"SyncedPrefab\",\"px\":1,\"py\":2,\"pz\":3,\"rw\":1,\"sx\":1,\"sy\":1,\"sz\":1,\"active\":true}",
            };
        }

        [Test]
        public void APrefabThatIsPresentArrivesWithoutAMarker()
        {
            GameObject source;
            var path = CreatePrefabAsset(out source);
            var guid = AssetDatabase.AssetPathToGUID(path);
            Assert.IsFalse(string.IsNullOrEmpty(guid));

            var registry = new SceneNodeRegistry();
            var applier = new SceneOpApplier(registry, new SceneDiff(), new SceneStructure(registry));

            string note;
            Assert.IsTrue(applier.Apply(AddNodeOp("a1b2c3d4e5f6a7b8", guid), out note), note);

            GameObject created;
            Assert.IsTrue(registry.TryResolve("a1b2c3d4e5f6a7b8", out created), "the node did not resolve");
            Assert.IsNotNull(created);
            Assert.IsTrue(PrefabUtility.IsPartOfPrefabInstance(created), "the prefab was not instantiated");
            Assert.AreEqual(0, CollabPlaceholders.For(created).Length,
                "an object that arrived as the real prefab must not carry an ERROR marker");

            Object.DestroyImmediate(created);
            Object.DestroyImmediate(source);
        }

        [Test]
        public void AHealthyPrefabIsNotMarkedByTheHealthScan()
        {
            GameObject source;
            var path = CreatePrefabAsset(out source);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var instance = PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene()) as GameObject;
            Assert.IsNotNull(instance);

            Assert.IsFalse(CollabAssetHealth.IsMissingMeshOrMaterial(instance),
                "a cube prefab with its material reads as broken");

            var report = CollabAssetHealth.Scan(instance.scene, null);
            Assert.AreEqual(0, report.Placeholders, "the scan marked " + report.Placeholders + " healthy object(s)");
            Assert.AreEqual(0, CollabPlaceholders.For(instance).Length);

            Object.DestroyImmediate(instance);
            Object.DestroyImmediate(source);
        }

        [Test]
        public void AScanMarkerForPrefabAssetsRetiresWhenTheGuidStartsResolving()
        {
            GameObject source;
            var path = CreatePrefabAsset(out source);
            var guid = AssetDatabase.AssetPathToGUID(path);

            var standIn = new GameObject("StandIn");
            CollabPlaceholders.AttachPrefabError(standIn, "StandIn (missing prefab)", guid);

            // The asset is in this project's database right now, so the very first refresh retires the
            // marker — this is the "asset arrived while the marker was up" case, without waiting.
            CollabPlaceholders.Revalidate();
            Assert.AreEqual(0, CollabPlaceholders.For(standIn).Length,
                "the marker for an asset that resolves on this machine stayed up");

            Object.DestroyImmediate(standIn);
            Object.DestroyImmediate(source);
        }

        [Test]
        public void AStandInForAnArrivingPrefabAsksToBeRebuilt()
        {
            GameObject source;
            var path = CreatePrefabAsset(out source);
            var guid = AssetDatabase.AssetPathToGUID(path);

            var standIn = new GameObject("StandIn");
            CollabPlaceholders.AttachPrefabError(standIn, "StandIn (missing prefab)", guid);

            GameObject asked = null;
            var askedGuid = string.Empty;
            CollabPlaceholders.Revalidate(delegate(GameObject owner, string prefabGuid)
            {
                asked = owner;
                askedGuid = prefabGuid;
            });

            Assert.AreEqual(0, CollabPlaceholders.For(standIn).Length, "the marker survived a resolved prefab");
            Assert.AreEqual(standIn, asked, "the stand-in was retired with nothing to replace it");
            Assert.AreEqual(guid, askedGuid, "the rebuild was handed the wrong prefab");

            Object.DestroyImmediate(standIn);
            Object.DestroyImmediate(source);
        }

        [Test]
        public void AStandInThatGrewContentOfItsOwnIsNotRebuilt()
        {
            GameObject source;
            var path = CreatePrefabAsset(out source);
            var guid = AssetDatabase.AssetPathToGUID(path);

            var standIn = new GameObject("StandIn");
            var child = new GameObject("SomebodysWork");
            child.transform.SetParent(standIn.transform);
            CollabPlaceholders.AttachPrefabError(standIn, "StandIn (missing prefab)", guid);

            var asked = false;
            CollabPlaceholders.Revalidate(delegate { asked = true; });

            Assert.AreEqual(0, CollabPlaceholders.For(standIn).Length);
            Assert.IsFalse(asked, "re-instantiating the prefab would have thrown away content added on top");
            Assert.AreEqual(1, standIn.transform.childCount);

            Object.DestroyImmediate(standIn);
            Object.DestroyImmediate(source);
        }
    }
}
