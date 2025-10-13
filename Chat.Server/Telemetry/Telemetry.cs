using Prometheus;

namespace Chat.Server.Telemetry;

public static class Telemetry
{
    // Contadores
    public static readonly Counter SocketSessionsOpened = Prometheus.Metrics.CreateCounter(
        "chat_socket_sessions_opened_total", "Sessões de socket abertas");
    public static readonly Counter SocketSessionsClosed = Prometheus.Metrics.CreateCounter(
        "chat_socket_sessions_closed_total", "Sessões de socket fechadas");
    public static readonly Counter MessagesIn = Prometheus.Metrics.CreateCounter(
        "chat_messages_in_total", "Mensagens recebidas pelo servidor", new CounterConfiguration { LabelNames = new[] { "type" } });
    public static readonly Counter MessagesOut = Prometheus.Metrics.CreateCounter(
        "chat_messages_out_total", "Mensagens entregues a clientes", new CounterConfiguration { LabelNames = new[] { "type" } });
    public static readonly Counter FileChunks = Prometheus.Metrics.CreateCounter(
        "chat_file_chunks_total", "Chunks de arquivo roteados");

    // Gauge
    public static readonly Gauge ActiveSessions = Prometheus.Metrics.CreateGauge(
        "chat_socket_active_sessions", "Sessões ativas agora");

    // Histogram (latência de entrega por tipo)
    public static readonly Histogram DeliveryLatency = Prometheus.Metrics.CreateHistogram(
        "chat_msg_delivery_seconds", "Latência fim-a-fim do roteamento",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(start: 0.005, factor: 2, count: 12), // ~5ms..>10s
            LabelNames = new[] { "type" } // private/group/file
        });
}
