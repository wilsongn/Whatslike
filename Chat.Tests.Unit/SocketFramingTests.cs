using System.Net;
using System.Net.Sockets;
using Chat.Shared.Net;
using FluentAssertions;

namespace Chat.Tests.Unit;

public class SocketFramingTests
{
    [Fact]
    public async Task Send_and_receive_frame_length_prefixed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var clientTask = Task.Run(async () =>
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await s.ConnectAsync(IPAddress.Loopback, port);
            var payload = System.Text.Encoding.UTF8.GetBytes("ping");
            await SocketFraming.SendFrameAsync(s, payload);
            s.Shutdown(SocketShutdown.Both);
        });

        using var server = await listener.AcceptSocketAsync();
        var frame = await SocketFraming.ReadFrameAsync(server, CancellationToken.None);
        System.Text.Encoding.UTF8.GetString(frame!).Should().Be("ping");

        await clientTask;
        listener.Stop();
    }
}
