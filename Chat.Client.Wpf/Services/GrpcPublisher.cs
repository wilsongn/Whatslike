using System;
using System.Net.Http;
using System.Threading.Tasks;
using Chat.Grpc;
using Grpc.Net.Client;

namespace Chat.Client.Wpf.Services;

public sealed class GrpcPublisher : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly ChatService.ChatServiceClient _client;

    public GrpcPublisher(string baseAddress) // ex.: https://localhost:6000
    {
        var handler = new HttpClientHandler
        {
            // !!! DEV APENAS: ignora nome/CA
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        _channel = GrpcChannel.ForAddress(baseAddress, new GrpcChannelOptions { HttpClient = http });
        _client = new ChatService.ChatServiceClient(_channel);
    }

    public async Task<bool> PublishPrivateAsync(string from, string to, string text)
    {
        var res = await _client.PublishPrivateAsync(new PublishPrivateRequest { From = from, To = to, Text = text });
        return string.Equals(res.Status, "ok", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> PublishGroupAsync(string from, string group, string text)
    {
        var res = await _client.PublishToGroupAsync(new PublishGroupRequest { From = from, Group = group, Text = text });
        return string.Equals(res.Status, "ok", StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync() => await _channel.ShutdownAsync();
}
