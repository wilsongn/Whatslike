namespace Chat.Api.Messaging;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string TopicMessages { get; set; } = "messages";
    public string ClientId { get; set; } = "chat-api";
}
