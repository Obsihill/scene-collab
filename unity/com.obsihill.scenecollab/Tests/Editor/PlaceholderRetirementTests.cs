using System.Collections.Generic;
using NUnit.Framework;
using SceneCollab.Editor.SceneSync;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// v0.5.3: an ERROR marker has to go away when the thing it complained about arrives.
    ///
    /// Marking alone was half a mechanism, and the missing half was visible to users: a prefab was marked
    /// while its asset was still importing (or before this project's asset database knew the guid), the
    /// asset arrived, the object then synchronised perfectly — and the magenta ERROR sign stayed on it,
    /// claiming content was missing that was right there. These tests pin the retirement pass: every reason
    /// is re-checked, and only the still-broken ones survive.
    /// </summary>
    public sealed class PlaceholderRetirementTests
    {
        const string TempFolder = "Assets/__collab_tests";

        readonly List<string> _createdAssets = new List<string>();
        readonly List<GameObject> _created = new List<GameObject>();
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
            for (var i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null)
                {
                    CollabPlaceholders.Clear(_created[i]);
                    Object.DestroyImmediate(_created[i]);
                }
            }

            _created.Clear();

            for (var i = 0; i < _createdAssets.Count; i++)
            {
                AssetDatabase.DeleteAsset(_createdAssets[i]);
            }

            _createdAssets.Clear();
            CollabPlaceholders.Revalidate();
        }

        GameObject Make(string name)
        {
            var gameObject = new GameObject(name);
            _created.Add(gameObject);
            return gameObject;
        }

        // ── prefab and reference markers ──────────────────────────────────────

        [Test]
        public void APrefabMarkerRetiresOnceTheAssetIsHere()
        {
            var subject = Make("Subject");
            var prefabSource = Make("PrefabSource");
            prefabSource.AddComponent<BoxCollider>();

            var path = TempFolder + "/Retire_" + (++_saved) + ".prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(prefabSource, path);
            _createdAssets.Add(path);
            var guid = AssetDatabase.AssetPathToGUID(path);
            Assert.IsFalse(string.IsNullOrEmpty(guid), "the test asset has no guid");

            // A marker that was hung up while the asset was not there yet, with the guid it waits for.
            CollabPlaceholders.AttachPrefabError(subject, "Subject (missing prefab)", "ffffffffffffffffffffffffffffffff");
            Assert.AreEqual(1, CollabPlaceholders.For(subject).Length, "the marker was not attached");
            CollabPlaceholders.Revalidate();
            Assert.AreEqual(1, CollabPlaceholders.For(subject).Length,
                "a marker must survive while its asset is still absent");

            // The asset that arrives is the one the marker was waiting for.
            CollabPlaceholders.Clear(subject);
            CollabPlaceholders.AttachPrefabError(subject, "Subject (missing prefab)", guid);
            Assert.AreEqual(1, CollabPlaceholders.For(subject).Length);

            CollabPlaceholders.Revalidate();
            Assert.AreEqual(0, CollabPlaceholders.For(subject).Length,
                "the ERROR marker outlived the prefab it was standing in for");

            Assert.IsNotNull(prefab);
        }

        [Test]
        public void AReferenceMarkerKeepsTheGuidItWaitsFor()
        {
            const string token = "ref:0123456789abcdef0123456789abcdef:42";

            Assert.AreEqual("0123456789abcdef0123456789abcdef", SceneReferenceCodec.AssetGuidOf(token),
                "the guid inside a reference token was not readable");
            Assert.IsNull(SceneReferenceCodec.AssetGuidOf(SceneReferenceCodec.NullToken));
            Assert.IsNull(SceneReferenceCodec.AssetGuidOf(SceneReferenceCodec.LocalToken));
            Assert.IsNull(SceneReferenceCodec.AssetGuidOf("node:abc"));

            string guid;
            long localFileId;
            Assert.IsTrue(SceneReferenceCodec.TryParseAssetToken(token, out guid, out localFileId),
                "a well-formed reference token was not parsed");
            Assert.AreEqual(42L, localFileId, "the sub-asset id of a reference token was lost");
            Assert.IsFalse(SceneReferenceCodec.TryParseAssetToken(SceneReferenceCodec.LocalToken, out guid,
                out localFileId));
        }

        // ── script markers ────────────────────────────────────────────────────

        [Test]
        public void AScriptMarkerRetiresWhenTheTypeLoads()
        {
            var subject = Make("ScriptSubject");

            CollabPlaceholders.AttachScriptError(subject, "missing script: Some.Package.That.IsNot.Installed",
                "Some.Package.That.IsNot.Installed");
            CollabPlaceholders.Revalidate();
            Assert.AreEqual(1, CollabPlaceholders.For(subject).Length,
                "a marker must survive while the type is unloadable");

            CollabPlaceholders.Clear(subject);
            CollabPlaceholders.AttachScriptError(subject, "missing script: " + typeof(AudioSource).FullName,
                typeof(AudioSource).FullName);
            CollabPlaceholders.Revalidate();
            Assert.AreEqual(0, CollabPlaceholders.For(subject).Length,
                "the marker stayed after the type became loadable");
        }

        // ── mesh/material markers ─────────────────────────────────────────────

        [Test]
        public void AMeshMarkerRetiresWhenTheMaterialArrives()
        {
            var subject = Make("BareRenderer");
            var renderer = subject.AddComponent<MeshRenderer>();

            Assert.IsTrue(CollabAssetHealth.IsMissingMeshOrMaterial(subject),
                "a renderer with no material must read as broken");

            CollabPlaceholders.AttachMeshError(subject, "missing mesh or material");
            CollabPlaceholders.Revalidate();
            Assert.AreEqual(1, CollabPlaceholders.For(subject).Length,
                "the marker retired while the renderer was still bare");

            var shader = Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
            Assert.IsNotNull(shader, "no shader available to build a test material");
            var material = new Material(shader);
            renderer.sharedMaterial = material;

            CollabPlaceholders.Revalidate();
            Assert.AreEqual(0, CollabPlaceholders.For(subject).Length,
                "the marker outlived the material it was complaining about");

            Object.DestroyImmediate(material);
        }

    }
}
