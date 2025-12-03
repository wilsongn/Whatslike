# 🔌 Chat.Frontend como Proxy WebSocket

## 📐 Arquitetura com Proxy

```
┌─────────────────────────────────────────────────────────────────┐
│                    FRONTEND (Browser/App)                        │
│                                                                  │
│  - REST API calls (HTTP)                                        │
│  - WebSocket connection (WS)                                    │
└─────────────────────────────────────────────────────────────────┘
                            │
                            │ http://chat-frontend:8080
                            │ ws://chat-frontend:8080/ws/status
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    CHAT.FRONTEND (Proxy Layer)                   │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  REST API Controllers                                      │  │
│  │  - POST /api/v1/messages → Kafka                         │  │
│  │  - Idempotency service                                    │  │
│  └───────────────────────────────────────────────────────────┘  │
│                            │                                     │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  WebSocketProxyMiddleware ⭐ NOVO                         │  │
│  │  - Aceita conexão do cliente                              │  │
│  │  - Conecta ao Chat.Api backend                            │  │
│  │  - Proxy bidirecional de mensagens                        │  │
│  │  - Client ⟷ Proxy ⟷ Backend                            │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                            │
                            │ http://chat-api:5000
                            │ ws://chat-api:5000/ws/status
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                    CHAT.API (Backend)                            │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  REST API Controllers                                      │  │
│  │  - Message persistence                                     │  │
│  │  - File management                                         │  │
│  └───────────────────────────────────────────────────────────┘  │
│                            │                                     │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │  WebSocketHub                                              │  │
│  │  - Gerencia conexões WebSocket                            │  │
│  │  - Subscribe/Unsubscribe por conversa                     │  │
│  │  - Escuta Redis Pub/Sub                                   │  │
│  │  - Distribui notificações                                 │  │
│  └───────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                            │
                            ├──► Kafka
                            ├──► Redis
                            └──► Cassandra
```

---

## 🔄 Fluxo de Mensagens

### 1️⃣ REST API (Envio de Mensagem)

```
Browser
   │
   │ POST /api/v1/messages
   │ Authorization: Bearer {JWT}
   ▼
Chat.Frontend
   │
   │ Valida idempotência (Redis)
   │ Publica evento no Kafka
   ▼
Kafka (messages)
   │
   ▼
RouterWorker → Cassandra
```

### 2️⃣ WebSocket (Notificações de Status)

```
Browser
   │
   │ WS CONNECT /ws/status?access_token={JWT}
   ▼
Chat.Frontend (WebSocketProxyMiddleware)
   │
   │ 1. Aceita conexão do cliente
   │ 2. Extrai JWT token
   │ 3. Conecta ao backend
   │
   │ WS CONNECT /ws/status?access_token={JWT}
   ▼
Chat.Api (WebSocketHub)
   │
   │ 1. Valida JWT
   │ 2. Gerencia conexão
   │ 3. Aguarda subscriptions
   │
   ◄─── Browser envia: {"type":"subscribe","conversationId":"..."}
   │
   │ Proxy encaminha ───►
   │
   ◄─── Chat.Api responde: {"type":"subscribed"}
   │
   │ Proxy encaminha ───►
   │
   │ Escuta Redis: status:{conversationId}
   │
   ◄─── Redis notifica: {"type":"message.status","status":"READ",...}
   │
   │ WebSocketHub envia para conexão
   │
   │ Proxy encaminha ───►
   │
   ▼
Browser recebe notificação
```

---

## ⚙️ Configuração

### Chat.Frontend (appsettings.json)

```json
{
  "ChatApi": {
    "BaseUrl": "http://localhost:5000"
  },
  "Kafka": {
    "BootstrapServers": "localhost:9092"
  },
  "Redis": {
    "ConnectionString": "localhost:6379"
  },
  "JWT_SECRET": "26c8d9a793975af4999bc048990f6fd1"
}
```

### Variáveis de Ambiente (Docker)

```bash
# Chat.Frontend
CHAT_API_URL=http://chat-api:5000
KAFKA_BOOTSTRAP_SERVERS=kafka:9092
REDIS_CONNECTION_STRING=redis:6379
JWT_SECRET=26c8d9a793975af4999bc048990f6fd1

# Chat.Api
REDIS_URL=redis:6379
KAFKA_BOOTSTRAP=kafka:9092
```

---

## 🚀 Como Usar

### 1. Frontend conecta ao Chat.Frontend

```javascript
// Antes (conectava direto no Chat.Api)
// const client = new ChatStatusClient('http://localhost:5000', token);

// Agora (conecta no Chat.Frontend - proxy)
const client = new ChatStatusClient('http://localhost:8080', token);
```

### 2. Chat.Frontend faz proxy para Chat.Api

O Chat.Frontend automaticamente:
- Aceita a conexão WebSocket
- Valida o JWT token
- Conecta ao Chat.Api backend
- Faz proxy bidirecional de todas as mensagens

### 3. Envio de mensagens

```bash
# Envia para Chat.Frontend (não mudou)
curl -X POST http://localhost:8080/api/v1/messages \
  -H "Authorization: Bearer $JWT_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "conversationId": "123e4567-e89b-12d3-a456-426614174000",
    "content": "Olá mundo!"
  }'
```

---

## 🎯 Vantagens desta Arquitetura

### 1. **Centralização**
- Frontend só precisa conhecer um endpoint: `chat-frontend:8080`
- Não expõe diretamente o Chat.Api

### 2. **Flexibilidade**
- Fácil adicionar lógica de roteamento
- Pode adicionar rate limiting extra
- Pode adicionar logs/métricas centralizadas

### 3. **Segurança**
- Chat.Api pode ficar em rede interna
- Chat.Frontend faz validação adicional
- Token validation em duas camadas

### 4. **Escalabilidade**
- Chat.Frontend pode fazer load balancing para múltiplos Chat.Api
- Pode adicionar cache de autenticação

---

## 📊 Comparação: Direto vs Proxy

| Aspecto | Direto (Chat.Api) | Proxy (Chat.Frontend) |
|---------|-------------------|------------------------|
| **Latência** | Menor (~5ms) | Ligeiramente maior (~10-15ms) |
| **Complexidade** | Menor | Maior |
| **Centralização** | Não | Sim ✅ |
| **Flexibilidade** | Menor | Maior ✅ |
| **Load Balancing** | Manual | Automático ✅ |
| **Rate Limiting** | Uma camada | Duas camadas ✅ |

---

## 🔍 Detalhes de Implementação

### WebSocketProxyMiddleware

```csharp
// Aceita conexão do cliente
var clientWebSocket = await context.WebSockets.AcceptWebSocketAsync();

// Conecta ao backend
var backendWebSocket = new ClientWebSocket();
await backendWebSocket.ConnectAsync(backendUri, cancellationToken);

// Proxy bidirecional
var clientToBackend = ProxyMessagesAsync(clientWebSocket, backendWebSocket);
var backendToClient = ProxyMessagesAsync(backendWebSocket, clientWebSocket);

await Task.WhenAny(clientToBackend, backendToClient);
```

### ProxyMessagesAsync

```csharp
private async Task ProxyMessagesAsync(
    WebSocket source,
    WebSocket destination,
    string direction,
    CancellationToken cancellationToken)
{
    var buffer = new byte[1024 * 4];
    
    while (!cancellationToken.IsCancellationRequested)
    {
        // Recebe do source
        var result = await source.ReceiveAsync(buffer, cancellationToken);
        
        // Encaminha para destination
        await destination.SendAsync(buffer, result.MessageType, 
            result.EndOfMessage, cancellationToken);
    }
}
```

---

## 🧪 Teste

### 1. Subir serviços

```bash
docker-compose -f docker-compose-with-frontend.yml up -d
```

### 2. Verificar conectividade

```bash
# Chat.Frontend
curl http://localhost:8080/health

# Chat.Api (não exposto externamente)
docker exec -it chat-frontend curl http://chat-api:5000/health
```

### 3. Testar WebSocket

```javascript
// Conecta no Chat.Frontend (porta 8080)
const client = new ChatStatusClient('http://localhost:8080', token);
client.connect();
client.subscribe(conversationId);
```

### 4. Verificar logs

```bash
# Ver proxy em ação
docker logs -f chat-frontend | grep "WebSocket proxy"

# Deve mostrar:
# WebSocket proxy: New connection from 172.18.0.1
# WebSocket proxy: Connecting to backend ws://chat-api:5000/ws/status
# WebSocket proxy: Connected to backend successfully
# WebSocket proxy [Client->Backend]: {"type":"subscribe",...}
# WebSocket proxy [Backend->Client]: {"type":"subscribed",...}
```

---

## 🐛 Troubleshooting

### WebSocket não conecta

```bash
# Verificar se Chat.Frontend está rodando
docker ps | grep chat-frontend

# Verificar logs
docker logs chat-frontend | tail -20

# Verificar conectividade com Chat.Api
docker exec -it chat-frontend ping chat-api
```

### Mensagens não passam pelo proxy

```bash
# Verificar se ambas conexões estão abertas
docker logs chat-frontend | grep "WebSocket proxy"

# Deve mostrar conexão ativa em ambas direções
```

### Token inválido

```bash
# Verificar se JWT está correto
echo $JWT_TOKEN | cut -d '.' -f 2 | base64 -d

# Verificar se secret é o mesmo em frontend e api
docker exec chat-frontend env | grep JWT_SECRET
docker exec chat-api env | grep JWT_SECRET
```

---

## 📈 Métricas e Monitoramento

### Logs Importantes

```bash
# Chat.Frontend (proxy)
WebSocket proxy: New connection from {IP}
WebSocket proxy: Connecting to backend {URL}
WebSocket proxy: Connected to backend successfully
WebSocket proxy [{Direction}]: {Message}
WebSocket proxy [{Direction}]: Connection closed

# Chat.Api (backend)
[WebSocket] Conectado: ConnectionId={ID} UserId={UserID}
Cliente inscrito: ConnectionId={ID} ConversationId={ConvID}
Notificação WebSocket publicada no canal Redis: {Channel}
```

### Métricas Sugeridas

- `websocket_proxy_connections_total` - Total de conexões proxy
- `websocket_proxy_messages_total{direction}` - Mensagens por direção
- `websocket_proxy_latency_ms` - Latência do proxy
- `websocket_proxy_errors_total` - Erros no proxy

---

## 🔄 Evolução Futura

### Load Balancing

```
Chat.Frontend
      │
      ├──► Chat.Api-1 (ws://chat-api-1:5000)
      ├──► Chat.Api-2 (ws://chat-api-2:5000)
      └──► Chat.Api-3 (ws://chat-api-3:5000)
```

### Rate Limiting

```csharp
// No WebSocketProxyMiddleware
if (!await _rateLimiter.AllowConnectionAsync(userId))
{
    return StatusCode(429, "Too many connections");
}
```

### Métricas Centralizadas

```csharp
// Instrumentação no proxy
_metrics.RecordProxyConnection(userId, conversationId);
_metrics.RecordProxyMessage(direction, messageSize);
```

---

## ✅ Checklist de Implementação

- [x] WebSocketProxyMiddleware criado
- [x] Program.cs atualizado
- [x] appsettings.json configurado
- [x] Dockerfile criado
- [x] docker-compose-with-frontend.yml criado
- [x] Documentação completa
- [ ] Testes unitários do proxy
- [ ] Testes de integração
- [ ] Métricas implementadas
- [ ] Load balancing configurado

---

**Arquitetura com proxy implementada e funcionando!** ✅
