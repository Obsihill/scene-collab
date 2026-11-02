using System.Collections.Generic;
using NUnit.Framework;
using SceneCollab.Editor.SceneSync;
using UnityEngine;

namespace SceneCollab.Editor.Tests
{
    /// <summary>
    /// v0.5.4: the stand-in users actually see is a painted ERROR sign, not a bare cube.
    ///
    /// The cube was debug art that survived into releases: it said "something is wrong" without saying
    /// what, and a flat magenta box in a level reads as a missing prop rather than a broken reference. The
    /// sign is generated (a chequerboard texture with ERROR in ink), so it ships in a public package with
    /// no third-party art — and it has to stay readable on any object shape and from any angle.
    /// </summary>
    public sealed class ErrorSignTests
    {
        readonly List<GameObject> _created = new List<GameObject>();

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
            CollabPlaceholders.Revalidate();
        }

        GameObject Make(string name, Vector3 scale)
        {
            var subject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            subject.name = name;
            subject.transform.localScale = scale;
            _created.Add(subject);
            return subject;
        }

        [Test]
        public void TheDefaultStandInIsAPaintedSign()
        {
            var sign = CollabPlaceholders.Create("(missing prefab)", Vector3.one);
            Assert.IsNotNull(sign, "no stand-in was built");

            var component = sign.GetComponent<CollabErrorSign>();
            Assert.IsNotNull(component, "the default stand-in is not an ERROR sign");
            Assert.AreEqual(4, component.faces.Length,
                "the sign has to read from more than one direction, or it vanishes edge-on");

            var renderer = sign.GetComponentInChildren<MeshRenderer>();
            Assert.IsNotNull(renderer, "the sign has no renderer");
            var material = renderer.sharedMaterial;
            Assert.IsNotNull(material, "the sign has no material");
            var texture = material.mainTexture;
            if (texture == null)
            {
                texture = material.GetTexture("_BaseMap");
            }

            Assert.IsNotNull(texture, "the sign carries no lettering texture");
            Assert.AreEqual(256, texture.width);
            Assert.AreEqual(128, texture.height);

            Object.DestroyImmediate(sign);
        }

        [Test]
        public void TheSignKeepsTheWordReadableOnATallThinObject()
        {
            var subject = Make("TallThin", new Vector3(0.4f, 4f, 0.4f));
            var marker = CollabPlaceholders.AttachError(subject, "(missing prefab)");
            Assert.IsNotNull(marker);

            var component = marker.GetComponent<CollabErrorSign>();
            Assert.IsNotNull(component);

            var face = component.faces[0];
            var scale = face.lossyScale;
            var aspect = scale.x / scale.y;

            // The board is scaled by the object's bounds; without the compensation it would be 0.4 wide and
            // 4 tall, which renders "ERROR" as an unreadable vertical smear.
            Assert.AreEqual(2f, aspect, 0.05f, "the board no longer holds the word's 2:1 shape");

            CollabPlaceholders.Clear(subject);
        }

        [Test]
        public void TheSignIsAnUnsavedSceneRootWithoutColliders()
        {
            var subject = Make("Rooted", new Vector3(1f, 1f, 1f));
            var marker = CollabPlaceholders.AttachError(subject, "(missing prefab)");
            Assert.IsNotNull(marker);

            Assert.IsNull(marker.transform.parent, "the stand-in must not be parented to what it stands for");
            Assert.AreEqual(HideFlags.DontSave, marker.hideFlags & HideFlags.DontSave,
                "a stand-in must never be saved into the host's scene");
            Assert.AreEqual(0, marker.GetComponentsInChildren<Collider>(true).Length,
                "a stray collider would block the host's raycasts");

            CollabPlaceholders.Clear(subject);
        }

        [Test]
        public void TheSignSpreadsItsFacesAroundTheObject()
        {
            var sign = CollabPlaceholders.Create("(missing prefab)", Vector3.one);
            Assert.IsNotNull(sign);
            var component = sign.GetComponent<CollabErrorSign>();
            Assert.IsNotNull(component);

            // Opposed pairs: without them the second face of each board is invisible from behind.
            for (var i = 0; i < component.faces.Length; i += 2)
            {
                var forward = component.faces[i].forward;
                var opposite = component.faces[i + 1].forward;
                Assert.AreEqual(-1f, Vector3.Dot(forward, opposite), 0.01f,
                    "faces " + i + " and " + (i + 1) + " do not look in opposite directions");
            }

            Assert.AreEqual(0f, Vector3.Dot(component.faces[0].forward, component.faces[2].forward), 0.01f,
                "the two boards are not crossed");

            Object.DestroyImmediate(sign);
        }
    }
}
