using System;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Editor
{
    /// <summary>Ordered protocol-v2 recovery and terrain lease messages.</summary>
    public interface ICollabControlSession
    {
        bool SendControl(WireMessage message);
        event Action<WireMessage> ControlReceived;
    }
}
