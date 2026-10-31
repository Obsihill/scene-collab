using System;
using System.Text;

namespace SceneCollab.Domain
{
    /// <summary>
    /// Converts between Unity's <c>GlobalObjectId</c> string form and the opaque node id
    /// that travels over the wire.
    ///
    /// Why not use the fileID: Unity reassigns fileIDs when a scene is saved, and they are
    /// only unique inside one file, so they cannot identify a node across two editors.
    /// <c>GlobalObjectId</c> is the editor's own stable handle (scene GUID + fileID +
    /// prefab context), which survives saves and reorderings — but it is long and leaks
    /// absolute paths for assets, so it is hashed before leaving the machine.
    ///
    /// Lives in Domain rather than Editor so the rules are testable without a scene.
    /// </summary>
    public static class StableNodeId
    {
        /// <summary>
        /// Prefix used by the sidecar (<c>Foo.unity.ids.json</c>) repair table. A node whose
        /// id carries this prefix was renamed by a human and must be trusted over the hash.
        /// </summary>
        public const string RepairedPrefix = "repair:";

        /// <summary>Upper bound on id length; keeps a hostile client from stuffing the roster.</summary>
        public const int MaxLength = 96;

        /// <summary>
        /// Hashes a GlobalObjectId string into a short, stable, path-free id.
        /// Deterministic across machines and Unity sessions.
        /// </summary>
        public static string FromGlobalObjectId(string globalObjectId)
        {
            if (string.IsNullOrWhiteSpace(globalObjectId))
            {
                throw new ArgumentException("globalObjectId is required", nameof(globalObjectId));
            }

            // FNV-1a over UTF-8: 64 bits, no dependencies, stable forever. Collisions at
            // realistic scene sizes (10^4 nodes) are ~3e-12, and the sidecar can repair one.
            var bytes = Encoding.UTF8.GetBytes(globalObjectId.Trim());
            ulong hash = 14695981039346656037UL;
            for (var i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 1099511628211UL;
            }

            return hash.ToString("x16");
        }

        /// <summary>
        /// True when <paramref name="nodeId"/> is well formed. Rejects the shapes that make a
        /// server trust a client's claim about which node it is editing.
        /// </summary>
        public static bool IsValid(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || nodeId.Length > MaxLength)
            {
                return false;
            }

            if (nodeId.StartsWith(RepairedPrefix, StringComparison.Ordinal))
            {
                return nodeId.Length > RepairedPrefix.Length;
            }

            // Hashes are lowercase hex; anything else must not silently pass as an id.
            for (var i = 0; i < nodeId.Length; i++)
            {
                var c = nodeId[i];
                var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!isHex)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
