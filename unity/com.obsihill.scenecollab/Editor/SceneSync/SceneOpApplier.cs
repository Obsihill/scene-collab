using SceneCollab.Contracts.Protocol;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Applies an op that came from another editor.
    ///
    /// The server is the total order, so a remote op is applied even when the local object has drifted
    /// from the op's <c>oldValue</c> — but the drift is counted and reported, because silently
    /// overwriting someone's local edit is exactly the failure mode this project exists to avoid.
    /// </summary>
    public sealed class SceneOpApplier
    {
        readonly SceneNodeRegistry _registry;
        readonly SceneDiff _diff;

        public SceneOpApplier(SceneNodeRegistry registry, SceneDiff diff)
        {
            _registry = registry;
            _diff = diff;
        }

        public int Applied { get; private set; }

        public int Ignored { get; private set; }

        /// <summary>Ops applied on top of a value the local editor had already changed differently.</summary>
        public int Overwritten { get; private set; }

        public bool Apply(WireMessage op, out string note)
        {
            note = null;

            if (op == null)
            {
                note = "empty op";
                return false;
            }

            if (op.opKind != OpKinds.SetProperty)
            {
                Ignored++;
                note = "unsupported kind: " + op.opKind;
                return false;
            }

            if (string.IsNullOrEmpty(op.propertyPath) || !ScenePropertyAccess.IsTracked(op.propertyPath))
            {
                Ignored++;
                note = "untracked property: " + op.propertyPath;
                return false;
            }

            GameObject gameObject;
            if (!_registry.TryResolve(op.nodeId, out gameObject) || gameObject == null)
            {
                Ignored++;
                note = "unknown node: " + op.nodeId;
                return false;
            }

            var known = _diff.GetKnownValue(op.nodeId, op.propertyPath);
            if (known != null && !SceneValueCodec.AreEqual(known, op.oldValue))
            {
                Overwritten++;
            }

            if (!ScenePropertyAccess.TryWrite(gameObject, op.propertyPath, op.newValue))
            {
                Ignored++;
                note = "could not write " + op.propertyPath;
                return false;
            }

            // Advance the snapshot so the change we just made does not look local.
            _diff.NoteRemoteValue(op.nodeId, op.propertyPath, op.newValue);

            var scene = gameObject.scene;
            if (scene.IsValid())
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }

            Applied++;
            return true;
        }
    }
}
