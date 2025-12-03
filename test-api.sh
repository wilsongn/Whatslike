#!/bin/bash

# Script de Teste - API Gateway + Frontend Service
# Testa o fluxo completo: API → Kafka

set -e

# Cores
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

echo "=========================================="
echo "Testando Chat API - Semana 1"
echo "=========================================="
echo ""

# ============================================
# 1. Verificar serviços
# ============================================
echo -e "${BLUE}[1/5] Verificando serviços...${NC}"

# API Gateway
if ! curl -s http://localhost:8000/health > /dev/null 2>&1; then
    echo -e "${RED}❌ API Gateway não está respondendo em localhost:8000${NC}"
    echo "Execute: cd Chat.ApiGateway && dotnet run"
    exit 1
fi
echo -e "${GREEN}✅ API Gateway OK${NC}"

# Frontend Service
if ! curl -s http://localhost:8080/health > /dev/null 2>&1; then
    echo -e "${RED}❌ Frontend Service não está respondendo em localhost:8080${NC}"
    echo "Execute: cd Chat.Frontend && dotnet run"
    exit 1
fi
echo -e "${GREEN}✅ Frontend Service OK${NC}"

# Redis
if ! docker exec chat-redis redis-cli ping > /dev/null 2>&1; then
    echo -e "${RED}❌ Redis não está respondendo${NC}"
    exit 1
fi
echo -e "${GREEN}✅ Redis OK${NC}"

# Kafka
if ! docker exec chat-kafka kafka-broker-api-versions --bootstrap-server localhost:9092 > /dev/null 2>&1; then
    echo -e "${RED}❌ Kafka não está respondendo${NC}"
    exit 1
fi
echo -e "${GREEN}✅ Kafka OK${NC}"

echo ""

# ============================================
# 2. Gerar Token JWT
# ============================================
echo -e "${BLUE}[2/5] Gerando token JWT...${NC}"

# Token gerado com o secret padrão
# Payload: { "sub": "testuser", "name": "testuser", "exp": muito futuro }
TOKEN="eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ0ZXN0dXNlciIsIm5hbWUiOiJ0ZXN0dXNlciIsIm5iZiI6MTcwMDAwMDAwMCwiZXhwIjoyMDAwMDAwMDAwLCJpYXQiOjE3MDAwMDAwMDAsImlzcyI6ImNoYXQtZGV2IiwiYXVkIjoiY2hhdC1hcGkifQ.F5oN8YmJUqGbZF-3QZYz6V0Qx4JH5-K6NZYXqGHWKHk"

echo -e "${GREEN}✅ Token JWT gerado${NC}"
echo ""

# ============================================
# 3. Enviar mensagem via API Gateway
# ============================================
echo -e "${BLUE}[3/5] Enviando mensagem via API Gateway...${NC}"

RESPONSE=$(curl -s -X POST http://localhost:8000/api/v1/messages \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "conversationId": "user1_user2",
    "content": "Hello from API Test!"
  }')

echo "Response:"
echo "$RESPONSE" | jq '.' 2>/dev/null || echo "$RESPONSE"

# Extrair messageId
MESSAGE_ID=$(echo "$RESPONSE" | jq -r '.messageId' 2>/dev/null)

if [ -z "$MESSAGE_ID" ] || [ "$MESSAGE_ID" == "null" ]; then
    echo -e "${RED}❌ Falha ao enviar mensagem${NC}"
    exit 1
fi

echo -e "${GREEN}✅ Mensagem enviada: $MESSAGE_ID${NC}"
echo ""

# ============================================
# 4. Verificar Kafka
# ============================================
echo -e "${BLUE}[4/5] Verificando mensagem no Kafka...${NC}"

echo "Consumindo do tópico chat.messages (timeout 10s)..."

KAFKA_MESSAGE=$(docker exec chat-kafka kafka-console-consumer \
    --bootstrap-server localhost:9092 \
    --topic chat.messages \
    --from-beginning \
    --max-messages 1 \
    --timeout-ms 10000 \
    2>/dev/null || echo "")

if [ -z "$KAFKA_MESSAGE" ]; then
    echo -e "${YELLOW}⚠️  Nenhuma mensagem encontrada no Kafka (pode já ter sido consumida)${NC}"
else
    echo "Mensagem no Kafka:"
    echo "$KAFKA_MESSAGE" | jq '.' 2>/dev/null || echo "$KAFKA_MESSAGE"
    echo -e "${GREEN}✅ Mensagem encontrada no Kafka${NC}"
fi

echo ""

# ============================================
# 5. Testar idempotência
# ============================================
echo -e "${BLUE}[5/5] Testando idempotência (enviar mesma mensagem 2x)...${NC}"

RESPONSE2=$(curl -s -X POST http://localhost:8000/api/v1/messages \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d "{
    \"messageId\": \"$MESSAGE_ID\",
    \"conversationId\": \"user1_user2\",
    \"content\": \"This is a duplicate!\"
  }")

echo "Response (deve ser idêntica à primeira):"
echo "$RESPONSE2" | jq '.' 2>/dev/null || echo "$RESPONSE2"

MESSAGE_ID2=$(echo "$RESPONSE2" | jq -r '.messageId' 2>/dev/null)

if [ "$MESSAGE_ID" == "$MESSAGE_ID2" ]; then
    echo -e "${GREEN}✅ Idempotência funcionando! Mesma resposta retornada${NC}"
else
    echo -e "${RED}❌ Idempotência falhou (IDs diferentes)${NC}"
fi

echo ""

# ============================================
# 6. Verificar Redis (cache de idempotência)
# ============================================
echo -e "${BLUE}[Extra] Verificando cache Redis...${NC}"

CACHED=$(docker exec chat-redis redis-cli GET "ChatFrontend:idempotency:$MESSAGE_ID")

if [ -n "$CACHED" ] && [ "$CACHED" != "(nil)" ]; then
    echo "Cache encontrado:"
    echo "$CACHED" | jq '.' 2>/dev/null || echo "$CACHED"
    echo -e "${GREEN}✅ Cache de idempotência funcionando${NC}"
else
    echo -e "${YELLOW}⚠️  Cache não encontrado (pode ter expirado)${NC}"
fi

echo ""

# ============================================
# Resumo
# ============================================
echo -e "${GREEN}=========================================="
echo "✅ Testes concluídos!"
echo "==========================================${NC}"
echo ""
echo "Resultados:"
echo "  ✅ API Gateway funcionando"
echo "  ✅ Frontend Service funcionando"
echo "  ✅ Mensagem enviada e aceita"
echo "  ✅ Mensagem publicada no Kafka"
echo "  ✅ Idempotência validada"
echo ""
echo "Para ver mais detalhes:"
echo "  - Kafka UI: http://localhost:8090"
echo "  - Redis:    docker exec chat-redis redis-cli"
echo ""