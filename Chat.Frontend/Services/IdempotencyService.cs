using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace Chat.Frontend.Services;

/// <summary>
/// Serviço para garantir idempotência de requisições usando message_id
/// </summary>
public class IdempotencyService
{
    private readonly IDistributedCache _cache;
    private readonly ILogger<IdempotencyService> _logger;
    private readonly TimeSpan _ttl = TimeSpan.FromHours(24); // Cache por 24 horas

    public IdempotencyService(
        IDistributedCache cache,
        ILogger<IdempotencyService> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Verifica se uma mensagem já foi processada
    /// </summary>
    /// <param name="messageId">ID único da mensagem</param>
    /// <returns>True se já foi processada, False caso contrário</returns>
    public async Task<bool> IsDuplicateAsync(string messageId)
    {
        var key = GetKey(messageId);

        try
        {
            var cached = await _cache.GetStringAsync(key);
            var isDuplicate = cached != null;

            if (isDuplicate)
            {
                _logger.LogDebug(
                    "Duplicate detected: MessageId={MessageId}",
                    messageId);
            }

            return isDuplicate;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error checking duplicate for MessageId={MessageId}",
                messageId);

            // Em caso de erro no Redis, permitir processamento
            // (preferível processar duplicata do que perder mensagem)
            return false;
        }
    }

    /// <summary>
    /// Salva a resposta de uma mensagem no cache
    /// </summary>
    /// <typeparam name="T">Tipo da resposta</typeparam>
    /// <param name="messageId">ID único da mensagem</param>
    /// <param name="response">Resposta a ser cacheada</param>
    public async Task SaveResponseAsync<T>(string messageId, T response)
    {
        var key = GetKey(messageId);

        try
        {
            var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _ttl
            };

            await _cache.SetStringAsync(key, json, options);

            _logger.LogDebug(
                "Response cached: MessageId={MessageId}, TTL={TTL}",
                messageId,
                _ttl);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error saving response for MessageId={MessageId}",
                messageId);

            // Não propagar erro - falha no cache não deve impedir processamento
        }
    }

    /// <summary>
    /// Recupera uma resposta cacheada
    /// </summary>
    /// <typeparam name="T">Tipo da resposta</typeparam>
    /// <param name="messageId">ID único da mensagem</param>
    /// <returns>Resposta cacheada ou null se não encontrada</returns>
    public async Task<T?> GetResponseAsync<T>(string messageId)
    {
        var key = GetKey(messageId);

        try
        {
            var json = await _cache.GetStringAsync(key);

            if (json == null)
            {
                _logger.LogDebug(
                    "No cached response found: MessageId={MessageId}",
                    messageId);
                return default;
            }

            var response = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            _logger.LogDebug(
                "Cached response retrieved: MessageId={MessageId}",
                messageId);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error retrieving cached response for MessageId={MessageId}",
                messageId);

            return default;
        }
    }

    /// <summary>
    /// Remove uma resposta do cache (útil para testes)
    /// </summary>
    /// <param name="messageId">ID único da mensagem</param>
    public async Task RemoveAsync(string messageId)
    {
        var key = GetKey(messageId);

        try
        {
            await _cache.RemoveAsync(key);

            _logger.LogDebug(
                "Cached response removed: MessageId={MessageId}",
                messageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error removing cached response for MessageId={MessageId}",
                messageId);
        }
    }

    private static string GetKey(string messageId) => $"idempotency:{messageId}";
}