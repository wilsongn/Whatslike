public sealed class WorkerKafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string TopicMessages { get; set; } = "messages";
    public string GroupId { get; set; } = "router-worker";
    public int Partitions { get; set; } = 6;
    public short ReplicationFactor { get; set; } = 1;
}
