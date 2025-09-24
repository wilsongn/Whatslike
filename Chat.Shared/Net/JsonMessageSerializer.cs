using Chat.Shared.Protocol;
using System.Text.Json;

namespace Chat.Shared.Net
{
    public static class JsonMessageSerializer
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public static string Serialize<T>(T obj) =>
            JsonSerializer.Serialize(obj, Options);

        public static T? Deserialize<T>(string json) =>
            JsonSerializer.Deserialize<T>(json, Options);
    }

    public static class ProtocolUtil
    {
        /// <summary>
        /// Helper para criar um Envelope já serializando o payload em JSON.
        /// </summary>
        public static Chat.Shared.Protocol.Envelope Make<T>(
            Chat.Shared.Protocol.MessageType type,
            string? from, string? to, T payload) =>
            new(type, from, to, JsonMessageSerializer.Serialize(payload));


        public static Envelope Ack(string note, string? to = null) =>
            Make(MessageType.Ack, "server", to, new AckMessage("ok", note));

        public static Envelope Error(string code, string message, string? to = null) =>
            Make(MessageType.Error, "server", to, new ErrorMessage(code, message));

    }
}
