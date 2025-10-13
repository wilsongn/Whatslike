using System;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Chat.Client.Wpf.Services;

public sealed class JwtClientInterceptor : Interceptor
{
    private readonly Func<string?> _getToken;
    public JwtClientInterceptor(Func<string?> getToken) => _getToken = getToken;

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request, ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var headers = context.Options.Headers ?? new Metadata();
        var token = _getToken();
        if (!string.IsNullOrWhiteSpace(token))
            headers.Add("Authorization", $"Bearer {token}");

        var opt = context.Options.WithHeaders(headers);
        var ctx = new ClientInterceptorContext<TRequest, TResponse>(context.Method, context.Host, opt);
        return base.AsyncUnaryCall(request, ctx, continuation);
    }
}
