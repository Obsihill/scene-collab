using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using SceneCollab.Contracts.Protocol;

namespace SceneCollab.Server.WebApi.WebSockets
{
    /// <summary>
    /// One JSON configuration for the whole server. The editor uses Unity's JsonUtility on
    /// the same DTO shape, so these options must never introduce renames or converters —
    /// field names on the wire are the contract.
    /// </summary>
    public static class WireJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string Serialize(WireMessage message)
        {
            return JsonSerializer.Serialize(message, Options);
        }

        public static WireMessage Deserialize(string json)
        {
            return JsonSerializer.Deserialize<WireMessage>(json, Options);
        }
    }
}
