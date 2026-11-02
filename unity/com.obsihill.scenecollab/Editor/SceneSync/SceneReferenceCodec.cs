using System;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace SceneCollab.Editor.SceneSync
{
    /// <summary>
    /// Object references on the wire.
    ///
    /// A reference is an identity, not a value, so it cannot travel as the number a serialized file
    /// holds: local file ids are only meaningful inside one asset database, and an instance id dies
    /// with the process. Two shapes cover everything an editor can mean:
    ///
    /// * <c>ref:&lt;guid&gt;:&lt;localFileId&gt;</c> — an asset (material, mesh, prefab, texture). The guid
    ///   survives a copy between machines; the local id picks the sub-asset (the second material inside
    ///   a model, say).
    /// * <c>node:&lt;nodeId&gt;[#&lt;ComponentType&gt;]</c> — something else in the same scene, addressed by
    ///   the same stable id the rest of the protocol uses.
    /// * <c>ref:null</c> — the field is deliberately empty.
    /// * <c>ref:local</c> — it points at an object that exists only on the sender (an unsaved object
    ///   outside the session). The receiver cannot resolve it, so it gets a visible ERROR stand-in
    ///   rather than a silent null.
    ///
    /// Both directions are exact or refused. Nothing here guesses: a failed write reports why, and the
    /// applier turns that into something the user can see in the scene view.
    /// </summary>
    public static class SceneReferenceCodec
    {
        public const string AssetPrefix = "ref:";
        public const string NodePrefix = "node:";
        public const string NullToken = "ref:null";
        public const string LocalToken = "ref:local";

        /// <summary>
        /// Splits a <c>ref:&lt;guid&gt;:&lt;fileId&gt;</c> token into the asset guid and the sub-asset id.
        ///
        /// Both halves matter to an ERROR marker: a guid that resolves says the asset arrived, but a sub-asset
        /// id that no longer exists (a prefab re-authored, a sprite re-imported) means the reference is still
        /// broken. Retiring on the guid alone would hide exactly the failure markers exist to show.
        /// </summary>
        public static bool TryParseAssetToken(string token, out string guid, out long localFileId)
        {
            guid = null;
            localFileId = 0;

            if (string.IsNullOrEmpty(token) || !token.StartsWith(AssetPrefix, StringComparison.Ordinal))
            {
                return false;
            }

            var body = token.Substring(AssetPrefix.Length);
            var colon = body.IndexOf(':');
            var guidPart = colon > 0 ? body.Substring(0, colon) : body;
            if (guidPart.Length != 32)
            {
                return false;
            }

            guid = guidPart;
            if (colon > 0)
            {
                long.TryParse(body.Substring(colon + 1), out localFileId);
            }

            return true;
        }

        /// <summary>
        /// The asset guid inside a <c>ref:&lt;guid&gt;:&lt;fileId&gt;</c> token, or null for the other forms. Used to
        /// remember which asset an ERROR marker is waiting for, so the marker can retire once it arrives.
        /// </summary>
        public static string AssetGuidOf(string token)
        {
            string guid;
            long localFileId;
            return TryParseAssetToken(token, out guid, out localFileId) ? guid : null;
        }

        public static bool IsReference(string value)
        {
            return value != null
                && (value.StartsWith(AssetPrefix, StringComparison.Ordinal)
                    || value.StartsWith(NodePrefix, StringComparison.Ordinal));
        }

        /// <summary>Encodes what this machine's field currently points at.</summary>
        public static bool TryRead(SerializedProperty property, SceneNodeRegistry registry, out string value)
        {
            value = null;
            if (property == null)
            {
                return false;
            }

            var target = property.objectReferenceValue;
            if (target == null)
            {
                value = NullToken;
                return true;
            }

            string guid;
            long localId = 0;
            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(target, out guid, out localId)
                && !string.IsNullOrEmpty(guid))
            {
                value = AssetPrefix + guid + ":" + localId.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            var gameObject = target as GameObject;
            string typeHint = null;
            if (gameObject == null)
            {
                var component = target as Component;
                if (component != null)
                {
                    gameObject = component.gameObject;
                    typeHint = target.GetType().FullName;
                }
            }

            string nodeId;
            if (gameObject != null && registry != null
                && registry.TryGetNodeId(gameObject, out nodeId) && !string.IsNullOrEmpty(nodeId))
            {
                value = NodePrefix + nodeId + (typeHint == null ? "" : "#" + typeHint);
                return true;
            }

            // Addressable by neither a guid nor a node id: the peer cannot have it.
            value = LocalToken;
            return true;
        }

        /// <summary>
        /// Resolves an encoded reference on this machine. A false return leaves the field untouched and
        /// fills <paramref name="problem"/> with a phrase fit for a status line.
        /// </summary>
        public static bool TryWrite(SerializedProperty property, string value, SceneNodeRegistry registry,
            out string problem)
        {
            problem = null;
            if (property == null)
            {
                problem = "no such field";
                return false;
            }

            if (string.IsNullOrEmpty(value))
            {
                value = NullToken;
            }

            if (string.Equals(value, NullToken, StringComparison.Ordinal))
            {
                property.objectReferenceValue = null;
                return true;
            }

            if (string.Equals(value, LocalToken, StringComparison.Ordinal))
            {
                problem = "reference points at an object the sender did not mirror";
                return false;
            }

            if (value.StartsWith(NodePrefix, StringComparison.Ordinal))
            {
                return TryWriteNode(property, value.Substring(NodePrefix.Length), registry, out problem);
            }

            if (value.StartsWith(AssetPrefix, StringComparison.Ordinal))
            {
                return TryWriteAsset(property, value.Substring(AssetPrefix.Length), out problem);
            }

            problem = "unknown reference form: " + value;
            return false;
        }

        static bool TryWriteNode(SerializedProperty property, string body, SceneNodeRegistry registry,
            out string problem)
        {
            problem = null;

            var typeHint = (string)null;
            var hash = body.IndexOf('#');
            if (hash >= 0)
            {
                typeHint = body.Substring(hash + 1);
                body = body.Substring(0, hash);
            }

            GameObject gameObject;
            if (registry == null || !registry.TryResolve(body, out gameObject) || gameObject == null)
            {
                // Not an error in itself: the node may arrive a moment later, and the sender may simply
                // have a stale mapping. Reported so the caller can retry or mark it.
                problem = "scene object " + body + " is not on this machine yet";
                return false;
            }

            UnityEngine.Object resolved = gameObject;
            if (!string.IsNullOrEmpty(typeHint))
            {
                var type = ResolveTypeName(typeHint);
                var component = type == null ? null : gameObject.GetComponent(type);
                if (component == null)
                {
                    problem = "missing component " + ShortTypeName(typeHint);
                    return false;
                }

                resolved = component;
            }

            return Assign(property, resolved, out problem);
        }

        static bool TryWriteAsset(SerializedProperty property, string body, out string problem)
        {
            problem = null;

            long localId = 0;
            var guid = body;
            var colon = body.LastIndexOf(':');
            if (colon > 0)
            {
                guid = body.Substring(0, colon);
                long.TryParse(body.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out localId);
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                problem = "missing asset " + guid;
                return false;
            }

            var asset = LoadByLocalId(path, localId);
            if (asset == null)
            {
                problem = "missing asset " + guid;
                return false;
            }

            return Assign(property, asset, out problem);
        }

        static UnityEngine.Object LoadByLocalId(string path, long localId)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(path);
            if (assets != null && assets.Length > 0)
            {
                if (localId == 0)
                {
                    return assets[0];
                }

                for (var i = 0; i < assets.Length; i++)
                {
                    var entry = assets[i];
                    if (entry == null)
                    {
                        continue;
                    }

                    string guid;
                    long id;
                    if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(entry, out guid, out id) && id == localId)
                    {
                        return entry;
                    }
                }
            }

            return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
        }

        /// <summary>Assigns only when the field's declared type accepts the object; Unity would otherwise null it silently.</summary>
        static bool Assign(SerializedProperty property, UnityEngine.Object resolved, out string problem)
        {
            problem = null;

            var expected = ExpectedType(property);
            if (expected != null && resolved != null && !expected.IsInstanceOfType(resolved))
            {
                if (resolved is GameObject && typeof(Component).IsAssignableFrom(expected))
                {
                    var component = ((GameObject)resolved).GetComponent(expected);
                    if (component == null)
                    {
                        problem = "object has no " + expected.Name;
                        return false;
                    }

                    resolved = component;
                }
                else
                {
                    problem = "type mismatch: field wants " + expected.Name;
                    return false;
                }
            }

            property.objectReferenceValue = resolved;
            return true;
        }

        /// <summary>Reads the component type out of a <c>PPtr&lt;$T&gt;</c> property type string.</summary>
        static Type ExpectedType(SerializedProperty property)
        {
            var declared = property.type;
            if (string.IsNullOrEmpty(declared))
            {
                return null;
            }

            var open = declared.IndexOf('<');
            var close = declared.LastIndexOf('>');
            if (open < 0 || close <= open + 1)
            {
                return null;
            }

            var inner = declared.Substring(open + 1, close - open - 1);
            if (inner.StartsWith("$", StringComparison.Ordinal))
            {
                inner = inner.Substring(1);
            }

            return ResolveTypeName(inner);
        }

        public static Type ResolveTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return null;
            }

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                Type found;
                try
                {
                    found = assemblies[i].GetType(typeName, false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        public static string ShortTypeName(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return "";
            }

            var dot = typeName.LastIndexOf('.');
            return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
        }
    }
}
