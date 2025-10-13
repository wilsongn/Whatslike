using Prometheus;

namespace Chat.Server.Grpc;

public sealed class GrpcMetrics
{
    public readonly Counter PublishedPrivate;
    public readonly Counter PublishedGroup;

    public GrpcMetrics()
    {
        PublishedPrivate = Prometheus.Metrics.CreateCounter(
            "chat_grpc_published_private_total",
            "Mensagens privadas publicadas via gRPC");

        PublishedGroup = Prometheus.Metrics.CreateCounter(
            "chat_grpc_published_group_total",
            "Mensagens de grupo publicadas via gRPC");
    }
}
