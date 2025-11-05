namespace Chat.Persistence.Options;

public sealed class CassandraOptions
{
    public string[] ContactPoints { get; set; } = new[] { "localhost" };
    public int Port { get; set; } = 9042;
    public string DataCenter { get; set; } = "dc1";
    public string Keyspace { get; set; } = "chatops";
    public string ReplicationClass { get; set; } = "SimpleStrategy";
    public int ReplicationFactor { get; set; } = 1;
    public string BucketStrategy { get; set; } = "Month";
}
