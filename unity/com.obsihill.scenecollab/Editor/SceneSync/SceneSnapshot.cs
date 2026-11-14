using System;
using System.Collections.Generic;
using SceneCollab.Contracts.Protocol;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Builds the frames that hand a late joiner the host's scene.
    ///
    /// Why a snapshot at all: the server keeps a bounded op history, and a room that has been open
    /// for an hour has far more edits than it holds. A guest that joins at minute 40 therefore sees
    /// only edits made after it arrived — the documented v0.4.2 hole. Replaying history cannot close
    /// it (the history may already have dropped the ops that created the scene), so the editor that
    /// owns the scene sends its current state instead.
    ///
    /// Why ops rather than a scene file: a .unity file would need matching project paths, matching
    /// asset GUIDs and a merge story for concurrent edits, and it would bypass the per-node identity
    /// the rest of the protocol is built on. Reusing the op shape means a snapshot is just
    /// "every op that would have produced this scene", applied by the same applier that handles live
    /// edits — one code path, one set of bugs.
    ///
    /// Cost is bounded on purpose: parents before children, one chunk per
    /// <see cref="MaxOpsPerChunk"/> ops, and a hard node ceiling so a pathological scene cannot
    /// stall the editor.
    /// </summary>
    public static class SceneSnapshot
    {
        /// <summary>Ops per <c>snapshot_chunk</c> frame. Small enough that one frame stays well under a megabyte.</summary>
        public const int MaxOpsPerChunk = 120;

        /// <summary>Refuse to snapshot absurd scenes rather than freeze both editors.</summary>
        public const int MaxNodes = 20000;

        /// <summary>
        /// Builds the full frame list for one scene: <c>snapshot_start</c>, N × <c>snapshot_chunk</c>,
        /// <c>snapshot_end</c>. Frames carry no revision, client or room — the session fills those in
        /// at send time, exactly as it does for a live op.
        /// </summary>
        public static bool TryBuild(Scene scene, SceneNodeRegistry registry, out List<WireMessage> frames, out string note)
        {
            frames = new List<WireMessage>();
            note = null;

            if (!scene.IsValid() || !scene.isLoaded)
            {
                note = "no scene to snapshot";
                return false;
            }

            if (registry == null)
            {
                note = "no node registry";
                return false;
            }

            var objects = new List<GameObject>();
            foreach (var gameObject in SceneNodeRegistry.EnumerateSceneObjects(scene))
            {
                if (gameObject != null)
                {
                    objects.Add(gameObject);
                }
            }

            if (objects.Count > MaxNodes)
            {
                note = "scene has " + objects.Count + " objects; the snapshot limit is " + MaxNodes;
                return false;
            }

            // Parents must exist before their children are added, so depth sorts the whole payload —
            // but only depth. Within one depth the enumeration order is the hierarchy order, and that is
            // the order the receiver must see: two objects whose names sort differently from their
            // sibling positions would otherwise arrive in a different arrangement than the host shows.
            var hierarchyOrder = new Dictionary<GameObject, int>(objects.Count);
            for (var i = 0; i < objects.Count; i++)
            {
                hierarchyOrder[objects[i]] = i;
            }

            objects.Sort((left, right) =>
            {
                var depthLeft = Depth(left);
                var depthRight = Depth(right);
                if (depthLeft != depthRight)
                {
                    return depthLeft.CompareTo(depthRight);
                }

                int orderLeft, orderRight;
                hierarchyOrder.TryGetValue(left, out orderLeft);
                hierarchyOrder.TryGetValue(right, out orderRight);
                return orderLeft.CompareTo(orderRight);
            });

            var ops = new List<WireMessage>(objects.Count * 4);
            var nodes = 0;

            for (var i = 0; i < objects.Count; i++)
            {
                var gameObject = objects[i];
                var nodeId = registry.EnsureNodeId(gameObject);
                if (string.IsNullOrEmpty(nodeId))
                {
                    continue;
                }

                AddNodeOps(registry, gameObject, nodeId, ops);
                nodes++;
            }

            var total = ops.Count == 0 ? 1 : (ops.Count + MaxOpsPerChunk - 1) / MaxOpsPerChunk;
            frames.Add(Frame(OpKinds.SnapshotStart, nodes.ToString()));

            for (var chunkIndex = 0; chunkIndex < total; chunkIndex++)
            {
                var start = chunkIndex * MaxOpsPerChunk;
                var count = Math.Min(MaxOpsPerChunk, ops.Count - start);
                var chunk = new List<WireMessage>(Math.Max(count, 0));
                for (var j = 0; j < count; j++)
                {
                    chunk.Add(ops[start + j]);
                }

                frames.Add(Frame(OpKinds.SnapshotChunk, JsonUtility.ToJson(new SnapshotChunk
                {
                    ops = chunk.ToArray(),
                    index = chunkIndex,
                    total = total,
                })));
            }

            frames.Add(Frame(OpKinds.SnapshotEnd, nodes.ToString()));
            note = nodes + " node(s), " + ops.Count + " op(s), " + total + " chunk(s)";
            return true;
        }

        static void AddNodeOps(SceneNodeRegistry registry, GameObject gameObject, string nodeId, List<WireMessage> ops)
        {
            string parentNodeId;
            if (!TryGetParentNodeId(registry, gameObject, out parentNodeId))
            {
                // Parent is outside the session (a prefab-internal child, for instance). Sending the
                // object would attach it to the scene root and silently move it, so it is skipped and
                // the parent's own payload covers it.
                return;
            }

            ops.Add(new WireMessage
            {
                snapshot = true,
                opKind = OpKinds.AddNode,
                nodeId = nodeId,
                parentNodeId = parentNodeId,
                siblingIndex = gameObject.transform.GetSiblingIndex(),
                prefabGuid = SceneStructure.PrefabGuidOf(gameObject),
                propertyPath = "",
                oldValue = "",
                newValue = JsonUtility.ToJson(SceneStructure.Capture(gameObject)),
            });

            // A prefab instance already carries its components; re-adding them is a no-op on the
            // receiver (ApplyAddComponent checks first), but a component added *to* the instance is an
            // override that only arrives this way.
            var components = gameObject.GetComponents<Component>();
            var slots = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null || component is Transform)
                {
                    continue;
                }

                var typeName = component.GetType().FullName;
                if (string.IsNullOrEmpty(typeName))
                {
                    continue;
                }

                // Same slot numbering the field paths use, so the add and the fields that follow agree
                // on which instance they mean even when the object carries two of the same type.
                int slot;
                slots.TryGetValue(typeName, out slot);
                slots[typeName] = slot + 1;

                ops.Add(new WireMessage
                {
                    snapshot = true,
                    opKind = OpKinds.AddComponent,
                    nodeId = nodeId,
                    componentIndex = slot,
                    propertyPath = "",
                    componentType = typeName,
                    oldValue = "",
                    newValue = typeName,
                });
            }

            var paths = new List<string>();
            SceneComponentFields.CollectPaths(gameObject, paths);
            for (var i = 0; i < ScenePropertyAccess.TrackedPaths.Length; i++)
            {
                paths.Add(ScenePropertyAccess.TrackedPaths[i]);
            }

            // Prefab instances go through the override kind so the receiver records the value as an
            // instance override; plain objects use set_property.
            var kind = PrefabUtility.IsPartOfPrefabInstance(gameObject)
                ? OpKinds.SetPrefabOverride
                : OpKinds.SetProperty;

            for (var i = 0; i < paths.Count; i++)
            {
                var path = paths[i];
                string value;
                if (!ScenePropertyAccess.TryRead(gameObject, path, registry, out value))
                {
                    continue;
                }

                ops.Add(new WireMessage
                {
                    snapshot = true,
                    opKind = kind,
                    nodeId = nodeId,
                    propertyPath = path,
                    oldValue = "",
                    newValue = value,
                });
            }
        }

        static bool TryGetParentNodeId(SceneNodeRegistry registry, GameObject gameObject, out string parentNodeId)
        {
            parentNodeId = "";
            var parent = gameObject.transform.parent;
            if (parent == null)
            {
                return true;
            }

            return registry.TryGetNodeId(parent.gameObject, out parentNodeId);
        }

        static int CompareByDepth(GameObject left, GameObject right)
        {
            var depthLeft = Depth(left);
            var depthRight = Depth(right);
            if (depthLeft != depthRight)
            {
                return depthLeft.CompareTo(depthRight);
            }

            return string.CompareOrdinal(left.name, right.name);
        }

        static int Depth(GameObject gameObject)
        {
            var depth = 0;
            var transform = gameObject == null ? null : gameObject.transform;
            while (transform != null && transform.parent != null)
            {
                depth++;
                transform = transform.parent;
            }

            return depth;
        }

        static WireMessage Frame(string kind, string value)
        {
            return new WireMessage
            {
                snapshot = true,
                opKind = kind,
                propertyPath = "",
                oldValue = "",
                newValue = value ?? "",
            };
        }
    }

    /// <summary>
    /// Receiving half of a snapshot: collects chunks, applies them through the normal applier, and
    /// reports which nodes the snapshot claimed so the caller can remove anything the host does not
    /// have.
    /// </summary>
    public sealed class SnapshotReceiver
    {
        readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);
        readonly List<WireMessage> _deferred = new List<WireMessage>();

        public bool Active { get; private set; }

        public int Chunks { get; private set; }

        public int TotalChunks { get; private set; }

        public int Ops { get; private set; }

        public int Nodes { get; private set; }

        public string LastError { get; private set; }
        public bool IsComplete { get { return Active && TotalChunks > 0 && Chunks == TotalChunks && _deferred.Count == 0; } }

        public void Begin()
        {
            _seen.Clear();
            _deferred.Clear();
            Active = true;
            Chunks = 0;
            TotalChunks = 0;
            Ops = 0;
            Nodes = 0;
            LastError = null;
        }

        /// <summary>Applies one chunk. Returns false when the chunk could not be read at all.</summary>
        public bool ApplyChunk(string json, SceneOpApplier applier)
        {
            if (!Active)
            {
                // A chunk without a start means the start frame was lost (it is one op in an ordered
                // stream, so this is a protocol bug, not a race). Ignore rather than apply blind.
                LastError = "chunk without snapshot_start";
                return false;
            }

            SnapshotChunk chunk;
            try
            {
                chunk = JsonUtility.FromJson<SnapshotChunk>(json);
            }
            catch (Exception ex)
            {
                LastError = "unreadable chunk: " + ex.Message;
                return false;
            }

            if (chunk == null || chunk.ops == null)
            {
                LastError = "empty chunk";
                return false;
            }

            if (chunk.total <= 0 || chunk.total > 100000 || chunk.index != Chunks
                || (TotalChunks != 0 && chunk.total != TotalChunks))
            {
                LastError = "snapshot chunks are missing, duplicated or out of order";
                return false;
            }

            Chunks++;
            TotalChunks = chunk.total;

            for (var i = 0; i < chunk.ops.Length; i++)
            {
                var op = chunk.ops[i];
                if (op == null)
                {
                    LastError = "null operation in snapshot";
                    return false;
                }

                op.snapshot = true;

                if (!string.IsNullOrEmpty(op.nodeId))
                {
                    _seen.Add(op.nodeId);
                }

                string note;
                if (applier.Apply(op, out note))
                {
                    Ops++;
                }
                else _deferred.Add(op); // Scene references may point at nodes in later chunks.
            }

            return true;
        }

        public bool TryComplete(SceneOpApplier applier)
        {
            if (!Active || TotalChunks == 0 || Chunks != TotalChunks)
            {
                LastError = "snapshot ended before every chunk arrived";
                return false;
            }
            for (var i = _deferred.Count - 1; i >= 0; i--)
            {
                string note;
                if (applier.Apply(_deferred[i], out note))
                {
                    Ops++;
                    _deferred.RemoveAt(i);
                }
                else LastError = "snapshot field could not be applied: " + note;
            }
            return _deferred.Count == 0;
        }

        /// <summary>Closes the snapshot and returns the node ids it claimed.</summary>
        public HashSet<string> End()
        {
            Active = false;
            Nodes = _seen.Count;
            return _seen;
        }

        public void Abort()
        {
            Active = false;
            _seen.Clear();
            _deferred.Clear();
        }
    }
}
