using System;

namespace SceneCollab.Contracts.Protocol
{
    /// <summary>
    /// A batch of ops that rebuilds part of the host's scene on a late joiner.
    ///
    /// The ops are the same <see cref="WireMessage"/> shape the live path uses, so the receiving
    /// side feeds them through the one applier instead of a second, subtly different rebuild
    /// routine. JsonUtility cannot deserialize a bare array, hence the wrapper.
    /// </summary>
    [Serializable]
    public sealed class SnapshotChunk
    {
        /// <summary>Ops in this chunk, in apply order (parents before children).</summary>
        public WireMessage[] ops;

        /// <summary>Zero-based chunk index, for the progress note.</summary>
        public int index;

        /// <summary>Chunks in this snapshot.</summary>
        public int total;
    }
}
