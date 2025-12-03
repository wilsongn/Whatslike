# Sistema de Atualização de Status via WebSocket

## 📋 Visão Geral

Este sistema implementa notificações em tempo real de status de mensagens (SENT → DELIVERED → READ) usando WebSocket. Quando um conector (WhatsApp/Instagram) atualiza o status de uma mensagem, o frontend é notificado instantaneamente.

## 🏗️ Arquitetura

```
┌─────────────┐      ┌──────────────┐      ┌─────────────┐
│   Frontend  │─────▶│   Chat.Api   │─────▶│    Kafka    │
│  (WebSocket)│      │  (WebSocket) │      │  (messages) │
└─────────────┘      └──────────────┘      └─────────────┘
       │                     │                      │
       │                     │                      ▼
       │                     │              ┌─────────────┐
       │                     │              │ RouterWorker│
       │                     │              │(persistence)│
       │                     │              └─────────────┘
       │                     │                      │
       │                     ▼                      ▼
       │             ┌──────────────┐      ┌─────────────┐
       │             │    Redis     │      │  Cassandra  │
       │             │  (Pub/Sub)   │      │  (storage)  │
       │             └──────────────┘      └─────────────┘
       │                     ▲
       │                     │
       └─────────────────────┘
              notificação
                                    
                                    ┌─────────────┐
                                    │  Connector  │
                                    │  WhatsApp   │─────┐
                                    └─────────────┘     │
                                                        │
                                    ┌─────────────┐     ▼
                                    │  Connector  │ ┌────────────┐
                                    │  Instagram  │─▶│   Kafka    │
                                    └─────────────┘ │ msg.status │
                                                    └────────────┘
                                                        │
                                                        ▼
                                                ┌─────────────────┐
                                                │  StatusWorker   │
                                                │ (consume status)│
                                                └─────────────────┘
                                                        │
                                                        ├──▶ Redis Pub/Sub
                                                        └──▶ Cassandra (update)
```

## 🔧 Componentes

### 1. **Chat.StatusWorker** (NOVO)
Worker que consome eventos de status do tópico Kafka `msg.status` e:
- Atualiza o status da mensagem no Cassandra quando é "READ"
- Publica notificação no Redis Pub/Sub no canal `status:{conversationId}`

### 2. **Chat.Api - WebSocket Hub** (NOVO)
- Endpoint WebSocket: `ws://localhost:5000/ws/status?access_token={JWT}`
- Gerencia conexões de clientes
- Permite inscrição em conversas específicas
- Escuta Redis Pub/Sub e encaminha notificações para clientes conectados

### 3. **Connectors Mock** (ATUALIZADO)
- Agora incluem `conversation_id` e `organizacao_id` nos eventos de status
- Publicam no tópico `msg.status` com informações completas

## 🚀 Como Usar

### Passo 1: Subir a infraestrutura

```bash
docker-compose up -d cassandra redis kafka zookeeper minio
```

Aguarde os serviços ficarem saudáveis (~30 segundos).

### Passo 2: Subir os workers e API

```bash
docker-compose up -d chat-api router-worker status-worker
```

### Passo 3: Subir os connectors

```bash
docker-compose up -d connector-whatsapp connector-instagram
```

### Passo 4: Obter um JWT Token

```bash
# Gerar token (exemplo simplificado)
# Use o endpoint de autenticação da sua API ou gere manualmente
JWT_TOKEN="seu-token-aqui"
```

### Passo 5: Conectar via WebSocket

#### Opção A: Usar a página de demonstração

1. Abra `frontend-example/demo.html` no navegador
2. Cole seu JWT token
3. Clique em "Conectar"
4. Insira um `conversation_id` e clique em "Inscrever"

#### Opção B: Usar JavaScript diretamente

```javascript
// Importar o cliente
const client = new ChatStatusClient('http://localhost:5000', 'seu-jwt-token');

// Registrar handlers
client.on('message.status', (data) => {
    console.log('Status atualizado:', data);
    // data.status = 'SENT' | 'DELIVERED' | 'READ'
    // data.messageId = '...'
    // data.conversationId = '...'
});

// Conectar
client.connect();

// Inscrever em uma conversa
client.subscribe('123e4567-e89b-12d3-a456-426614174000');
```

### Passo 6: Testar o fluxo completo

1. **Enviar uma mensagem via API:**

```bash
curl -X POST http://localhost:5000/v1/messages \
  -H "Authorization: Bearer $JWT_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "conversaId": "123e4567-e89b-12d3-a456-426614174000",
    "organizacaoId": "456e7890-e89b-12d3-a456-426614174000",
    "usuarioRemetenteId": "789e1234-e89b-12d3-a456-426614174000",
    "tipo": "text",
    "conteudo": {
      "texto": "Olá, mundo!"
    },
    "direcao": "outbound"
  }'
```

2. **Observar os logs:**

```bash
# Logs do connector (simula envio)
docker logs -f connector-whatsapp

# Logs do status worker
docker logs -f status-worker

# Você verá:
# [WHATSAPP] -> usuário X | texto
# [CALLBACK] SENT -> 202
# [CALLBACK] DELIVERED -> 202
# [CALLBACK] READ -> 202
```

3. **Verificar no Frontend:**
   - A página demo.html mostrará as atualizações de status em tempo real
   - SENT → DELIVERED → READ (com delays de 400ms e 800ms)

## 📡 Protocolo WebSocket

### Mensagens do Cliente para Servidor

#### 1. Inscrever em conversa
```json
{
  "type": "subscribe",
  "conversationId": "123e4567-e89b-12d3-a456-426614174000"
}
```

#### 2. Desinscrever de conversa
```json
{
  "type": "unsubscribe",
  "conversationId": "123e4567-e89b-12d3-a456-426614174000"
}
```

#### 3. Ping (manter conexão ativa)
```json
{
  "type": "ping"
}
```

### Mensagens do Servidor para Cliente

#### 1. Conectado
```json
{
  "type": "connected",
  "connectionId": "abc123...",
  "userId": "789e1234-e89b-12d3-a456-426614174000",
  "timestamp": "2025-01-15T10:30:00Z"
}
```

#### 2. Inscrito
```json
{
  "type": "subscribed",
  "conversationId": "123e4567-e89b-12d3-a456-426614174000",
  "timestamp": "2025-01-15T10:30:05Z"
}
```

#### 3. Status de mensagem (PRINCIPAL)
```json
{
  "type": "message.status",
  "messageId": "abc123def456...",
  "conversationId": "123e4567-e89b-12d3-a456-426614174000",
  "status": "READ",
  "channel": "whatsapp",
  "timestamp": "2025-01-15T10:30:10Z"
}
```

#### 4. Pong
```json
{
  "type": "pong",
  "timestamp": "2025-01-15T10:30:15Z"
}
```

#### 5. Erro
```json
{
  "type": "error",
  "message": "Mensagem inválida"
}
```

## 🔐 Autenticação

O WebSocket usa o mesmo JWT do REST API. Você pode passar o token de duas formas:

1. **Query String (recomendado para WebSocket):**
   ```
   ws://localhost:5000/ws/status?access_token=eyJhbGc...
   ```

2. **Header Authorization (REST API):**
   ```
   Authorization: Bearer eyJhbGc...
   ```

O token deve conter as claims:
- `sub` ou `nameid`: User ID (GUID)
- `tenant_id`: Organization ID (GUID) - opcional

## 📊 Monitoramento

### Logs importantes

```bash
# Status Worker
docker logs -f status-worker | grep "Status recebido"

# Redis Pub/Sub
docker exec -it redis redis-cli
> PSUBSCRIBE status:*

# Kafka (status topic)
docker exec -it kafka kafka-console-consumer \
  --bootstrap-server localhost:9092 \
  --topic msg.status \
  --from-beginning
```

### Métricas

- WebSocket: Conexões ativas, mensagens enviadas/recebidas
- Redis: Mensagens publicadas em `status:*`
- Kafka: Lag do consumer group `status-worker`

## 🐛 Troubleshooting

### WebSocket não conecta

1. Verifique se o JWT é válido:
```bash
# Decodificar token
echo "eyJhbGc..." | base64 -d
```

2. Verifique se a API está rodando:
```bash
curl http://localhost:5000/health
```

3. Verifique logs da API:
```bash
docker logs chat-api | grep WebSocket
```

### Notificações não chegam

1. Verifique se o StatusWorker está rodando:
```bash
docker logs status-worker | tail -20
```

2. Verifique se há mensagens no tópico Kafka:
```bash
docker exec -it kafka kafka-console-consumer \
  --bootstrap-server localhost:9092 \
  --topic msg.status \
  --from-beginning
```

3. Verifique se o Redis está publicando:
```bash
docker exec -it redis redis-cli
> PSUBSCRIBE status:*
# Envie uma mensagem e veja se aparece aqui
```

### Cliente não recebe atualizações

1. Verifique se está inscrito na conversa correta:
```javascript
console.log(client.subscriptions); // Deve conter o conversationId
```

2. Verifique se a mensagem tem o mesmo conversationId:
```bash
# No evento Kafka, conversation_id deve bater
```

3. Abra o console do navegador e procure por erros

## 🔄 Fluxo Completo

1. **Cliente conecta ao WebSocket**
   ```
   Frontend → ws://chat-api:5000/ws/status?access_token=...
   ```

2. **Cliente se inscreve em uma conversa**
   ```json
   { "type": "subscribe", "conversationId": "..." }
   ```

3. **Usuário envia mensagem**
   ```
   Frontend → POST /v1/messages → Kafka (messages)
   ```

4. **RouterWorker persiste a mensagem**
   ```
   Kafka (messages) → RouterWorker → Cassandra
   ```

5. **Connector recebe e simula envio**
   ```
   Kafka (msg.out.whatsapp) → Connector → Simula envio
   ```

6. **Connector publica status**
   ```
   Connector → Kafka (msg.status) com SENT, DELIVERED, READ
   ```

7. **StatusWorker processa e notifica**
   ```
   Kafka (msg.status) → StatusWorker → Redis Pub/Sub (status:conversationId)
   ```

8. **WebSocketHub entrega ao cliente**
   ```
   Redis Pub/Sub → WebSocketHub → Cliente WebSocket
   ```

9. **Frontend atualiza UI**
   ```javascript
   client.on('message.status', (data) => {
     updateMessageUI(data.messageId, data.status);
   });
   ```

## 📝 Notas Importantes

1. **Escalabilidade**: Múltiplas instâncias do Chat.Api podem rodar simultaneamente. O Redis Pub/Sub garante que a notificação chegue em todas.

2. **Reconexão automática**: O cliente JavaScript implementa reconexão automática com backoff exponencial.

3. **Heartbeat**: Ping/Pong a cada 30 segundos mantém a conexão ativa.

4. **Segurança**: JWT obrigatório. O WebSocket valida o token antes de aceitar a conexão.

5. **Performance**: O Redis Pub/Sub é extremamente rápido. Latência típica < 10ms.

## 🎯 Próximos Passos

- [ ] Implementar persistência de status READ no Cassandra
- [ ] Adicionar índice secundário para buscar mensagem por message_id
- [ ] Implementar rate limiting no WebSocket
- [ ] Adicionar métricas Prometheus
- [ ] Criar dashboard Grafana
- [ ] Implementar testes de carga
- [ ] Documentar OpenAPI do WebSocket

## 📚 Referências

- [WebSocket API](https://developer.mozilla.org/en-US/docs/Web/API/WebSocket)
- [Redis Pub/Sub](https://redis.io/docs/manual/pubsub/)
- [Kafka Consumers](https://kafka.apache.org/documentation/#consumerapi)
- [ASP.NET Core WebSockets](https://docs.microsoft.com/en-us/aspnet/core/fundamentals/websockets)
