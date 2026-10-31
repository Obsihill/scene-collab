using UnityEngine;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Editor
{
    /// <summary>
    /// The wire codec for the Unity side.
    ///
    /// <c>JsonUtility</c> rather than System.Text.Json: Unity's .NET profile does not ship
    /// System.Text.Json, and adding it as a dependency would make a "paste one line into the
    /// manifest" package need a NuGet step. The contract types are flat public-field classes
    /// precisely so this works.
    ///
    /// Known and accepted difference: JsonUtility writes absent strings as <c>""</c>, never
    /// <c>null</c>. The server treats empty and missing alike for these fields.
    /// </summary>
    internal static class UnityWireJson
    {
        public static string Serialize(WireMessage message)
        {
            return JsonUtility.ToJson(message);
        }

        public static WireMessage Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return null;
            }

            return JsonUtility.FromJson<WireMessage>(json);
        }
    }
}
