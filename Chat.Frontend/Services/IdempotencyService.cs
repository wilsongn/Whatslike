// Chat.Frontend/Services/IdempotencyService.cs
// ARQUIVO COMPLETO - Substituir todo o conteúdo

using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace Chat.Frontend.Services;

public class IdempotencyService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<IdempotencyService> _logger;
    private readonly TimeSpan _expiration = TimeSpan.FromHours(24);

    public IdempotencyService(IDistributedCache cache, ILogger<IdempotencyService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Verifica se a mensagem já foi processada
    /// </summary>
    public async Task<bool> IsDuplicateAsync(string messageId)
    {
        try
        {
            var key = $"idempotency:{messageId}";
            var cached = await _cache.GetStringAsync(key);
            return cached != null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking duplicate for MessageId={MessageId}", messageId);
            throw;
        }
    }

    /// <summary>
    /// Marca a mensagem como processada
    /// </summary>
    public async Task SetProcessedAsync(string messageId)
    {
        try
        {
            var key = $"idempotency:{messageId}";
            var value = JsonSerializer.Serialize(new
            {
                processedAt = DateTimeOffset.UtcNow,
                messageId
            });

            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _expiration
            };

            await _cache.SetStringAsync(key, value, options);
            _logger.LogDebug("Message marked as processed: MessageId={MessageId}", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking message as processed: MessageId={MessageId}", messageId);
            throw;
        }
    }

    /// <summary>
    /// Alias para SetProcessedAsync (compatibilidade)
    /// </summary>
    public Task MarkAsProcessedAsync(string messageId) => SetProcessedAsync(messageId);

    /// <summary>
    /// Remove uma mensagem do cache de idempotência
    /// </summary>
    public async Task RemoveAsync(string messageId)
    {
        try
        {
            var key = $"idempotency:{messageId}";
            await _cache.RemoveAsync(key);
            _logger.LogDebug("Message removed from idempotency cache: MessageId={MessageId}", messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing message from cache: MessageId={MessageId}", messageId);
            throw;
        }
    }
}