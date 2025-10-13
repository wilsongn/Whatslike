using System;
using System.Threading.Tasks;
using Grpc.Core;
using Chat.Grpc;
using Chat.Shared.Net;
using Chat.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Grpc;

public sealed class ChatGrpcService : ChatService.ChatServiceBase
{
    private readonly ConnectionTable _table;
    private readonly GrpcMetrics _metrics;
    private readonly ILogger<ChatGrpcService> _log;

    public ChatGrpcService(ConnectionTable table, GrpcMetrics metrics, ILogger<ChatGrpcService> log)
    {
        _table = table;
        _metrics = metrics;
        _log = log;
    }

    public override async Task<PublishAck> PublishPrivate(PublishPrivateRequest request, ServerCallContext context)
    {
        try
        {
            var env = ProtocolUtil.Make(MessageType.PrivateMsg, request.From, request.To, new ChatMessage(request.Text));
            await _table.DeliverPrivateAsync(env);
            _metrics.PublishedPrivate.Inc();
            return new PublishAck { Status = "ok" };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PublishPrivate failed");
            return new PublishAck { Status = "error", Detail = ex.Message };
        }
    }

    public override async Task<PublishAck> PublishToGroup(PublishGroupRequest request, ServerCallContext context)
    {
        try
        {
            var env = ProtocolUtil.Make(MessageType.GroupMsg, request.From, request.Group, new ChatMessage(request.Text));
            await _table.DeliverGroupAsync(env, request.Group);
            _metrics.PublishedGroup.Inc();
            return new PublishAck { Status = "ok" };
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PublishToGroup failed");
            return new PublishAck { Status = "error", Detail = ex.Message };
        }
    }
}
