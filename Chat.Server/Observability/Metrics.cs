using System.Threading;

namespace Chat.Server;

public static class Metrics
{
    public static long ConnectionsOpened;
    public static long ConnectionsClosed;
    public static long PrivateMsgs;
    public static long GroupMsgs;
    public static long LocalDeliveries;     // quantas entregas foram feitas a sockets locais
    public static long RemotePublishes;     // quantas publicações no bus (entre nós)
    public static long BusDelivered;        // quantas entregas vieram do bus
    public static long FileChunksForwarded; // cabeçalhos/chunks encaminhados

    public static string Snapshot(ConnectionTable table) =>
        $"active={table.ActiveCount} opened={Interlocked.Read(ref ConnectionsOpened)} " +
        $"closed={Interlocked.Read(ref ConnectionsClosed)} priv={Interlocked.Read(ref PrivateMsgs)} " +
        $"group={Interlocked.Read(ref GroupMsgs)} local={Interlocked.Read(ref LocalDeliveries)} " +
        $"pub={Interlocked.Read(ref RemotePublishes)} busIn={Interlocked.Read(ref BusDelivered)} " +
        $"fileChunks={Interlocked.Read(ref FileChunksForwarded)}";
}
