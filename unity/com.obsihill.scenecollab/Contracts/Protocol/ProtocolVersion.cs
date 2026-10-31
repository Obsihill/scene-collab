namespace SceneCollab.Contracts.Protocol
{
    /// <summary>
    /// Wire protocol version. The server refuses clients outside
    /// [MinCompatible, Current] instead of failing later with garbage state.
    /// Bump <see cref="Current"/> and <see cref="MinCompatible"/> independently.
    /// </summary>
    public static class ProtocolVersion
    {
        public const int Current = 1;
        public const int MinCompatible = 1;

        public static bool IsCompatible(int clientVersion)
        {
            return clientVersion >= MinCompatible && clientVersion <= Current;
        }
    }
}
