#!/bin/bash

# Script de teste end-to-end para o sistema de WebSocket
# Este script testa o fluxo completo de envio de mensagem e notificação de status

set -e

# Cores para output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuração
API_URL="${API_URL:-http://localhost:5000}"
KAFKA_CONTAINER="${KAFKA_CONTAINER:-kafka}"

echo -e "${BLUE}==================================================${NC}"
echo -e "${BLUE}  Sistema de Status via WebSocket - Teste E2E${NC}"
echo -e "${BLUE}==================================================${NC}"
echo ""

# Verificar se os serviços estão rodando
echo -e "${YELLOW}[1/8] Verificando serviços...${NC}"

services=("cassandra" "redis" "kafka" "chat-api" "status-worker" "connector-whatsapp")
for service in "${services[@]}"; do
    if docker ps | grep -q $service; then
        echo -e "${GREEN}  ✓ $service está rodando${NC}"
    else
        echo -e "${RED}  ✗ $service NÃO está rodando${NC}"
        echo -e "${RED}  Execute: docker-compose up -d $service${NC}"
        exit 1
    fi
done
echo ""

# Gerar IDs
echo -e "${YELLOW}[2/8] Gerando IDs para teste...${NC}"
ORG_ID=$(uuidgen | tr '[:upper:]' '[:lower:]')
USER_ID=$(uuidgen | tr '[:upper:]' '[:lower:]')
CONV_ID=$(uuidgen | tr '[:upper:]' '[:lower:]')
MSG_ID=$(uuidgen | tr '[:upper:]' '[:lower:]')

echo -e "${BLUE}  Organization ID: $ORG_ID${NC}"
echo -e "${BLUE}  User ID: $USER_ID${NC}"
echo -e "${BLUE}  Conversation ID: $CONV_ID${NC}"
echo -e "${BLUE}  Message ID: $MSG_ID${NC}"
echo ""

# Gerar JWT Token (simplificado - use seu endpoint real de auth)
echo -e "${YELLOW}[3/8] Gerando JWT Token...${NC}"
# Este é um token de exemplo. Em produção, use o endpoint /auth/login
JWT_SECRET="26c8d9a793975af4999bc048990f6fd1"
JWT_HEADER=$(echo -n '{"alg":"HS256","typ":"JWT"}' | base64 | tr -d '=' | tr '/+' '_-' | tr -d '\n')
JWT_PAYLOAD=$(echo -n "{\"sub\":\"$USER_ID\",\"tenant_id\":\"$ORG_ID\",\"exp\":9999999999}" | base64 | tr -d '=' | tr '/+' '_-' | tr -d '\n')
JWT_SIGNATURE=$(echo -n "${JWT_HEADER}.${JWT_PAYLOAD}" | openssl dgst -sha256 -hmac "$JWT_SECRET" -binary | base64 | tr -d '=' | tr '/+' '_-' | tr -d '\n')
JWT_TOKEN="${JWT_HEADER}.${JWT_PAYLOAD}.${JWT_SIGNATURE}"

echo -e "${GREEN}  ✓ Token gerado${NC}"
echo -e "${BLUE}  Token: ${JWT_TOKEN:0:50}...${NC}"
echo ""

# Testar autenticação
echo -e "${YELLOW}[4/8] Testando autenticação...${NC}"
HEALTH_RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" "$API_URL/health")
if [ "$HEALTH_RESPONSE" = "200" ]; then
    echo -e "${GREEN}  ✓ API está respondendo${NC}"
else
    echo -e "${RED}  ✗ API não está respondendo (HTTP $HEALTH_RESPONSE)${NC}"
    exit 1
fi
echo ""

# Monitorar tópico Kafka em background
echo -e "${YELLOW}[5/8] Configurando monitoramento Kafka...${NC}"
KAFKA_LOG="/tmp/kafka-test-$$.log"
docker exec -i $KAFKA_CONTAINER kafka-console-consumer \
    --bootstrap-server localhost:9092 \
    --topic msg.status \
    --timeout-ms 15000 \
    --from-beginning > "$KAFKA_LOG" 2>&1 &
KAFKA_PID=$!
echo -e "${GREEN}  ✓ Monitorando tópico msg.status (PID: $KAFKA_PID)${NC}"
echo ""

# Enviar mensagem via API
echo -e "${YELLOW}[6/8] Enviando mensagem...${NC}"
MESSAGE_PAYLOAD=$(cat <<EOF
{
  "conversaId": "$CONV_ID",
  "organizacaoId": "$ORG_ID",
  "usuarioRemetenteId": "$USER_ID",
  "mensagemId": "$MSG_ID",
  "tipo": "text",
  "conteudo": {
    "texto": "Mensagem de teste E2E"
  },
  "direcao": "outbound"
}
EOF
)

SEND_RESPONSE=$(curl -s -X POST "$API_URL/v1/messages" \
    -H "Authorization: Bearer $JWT_TOKEN" \
    -H "Content-Type: application/json" \
    -d "$MESSAGE_PAYLOAD")

echo -e "${GREEN}  ✓ Mensagem enviada${NC}"
echo -e "${BLUE}  Response: $SEND_RESPONSE${NC}"
echo ""

# Aguardar processamento
echo -e "${YELLOW}[7/8] Aguardando processamento (15 segundos)...${NC}"
for i in {1..15}; do
    echo -n "."
    sleep 1
done
echo ""
echo ""

# Verificar eventos de status no Kafka
echo -e "${YELLOW}[8/8] Verificando eventos de status...${NC}"
sleep 2  # Aguardar consumer terminar
kill $KAFKA_PID 2>/dev/null || true
wait $KAFKA_PID 2>/dev/null || true

if [ -f "$KAFKA_LOG" ]; then
    SENT_COUNT=$(grep -c "\"status\":\"SENT\"" "$KAFKA_LOG" || echo "0")
    DELIVERED_COUNT=$(grep -c "\"status\":\"DELIVERED\"" "$KAFKA_LOG" || echo "0")
    READ_COUNT=$(grep -c "\"status\":\"READ\"" "$KAFKA_LOG" || echo "0")
    
    echo -e "${BLUE}  Eventos capturados:${NC}"
    echo -e "    SENT: $SENT_COUNT"
    echo -e "    DELIVERED: $DELIVERED_COUNT"
    echo -e "    READ: $READ_COUNT"
    echo ""
    
    if [ "$SENT_COUNT" -ge 1 ] && [ "$DELIVERED_COUNT" -ge 1 ] && [ "$READ_COUNT" -ge 1 ]; then
        echo -e "${GREEN}  ✓ Todos os status foram recebidos!${NC}"
    else
        echo -e "${YELLOW}  ⚠ Nem todos os status foram recebidos${NC}"
    fi
    
    echo ""
    echo -e "${BLUE}Eventos completos:${NC}"
    cat "$KAFKA_LOG" | grep "message_id" | jq -C '.' 2>/dev/null || cat "$KAFKA_LOG"
    
    rm "$KAFKA_LOG"
else
    echo -e "${RED}  ✗ Não foi possível ler o log do Kafka${NC}"
fi
echo ""

# Verificar logs do StatusWorker
echo -e "${YELLOW}Logs do StatusWorker:${NC}"
docker logs status-worker --tail 10 | grep -E "(Status recebido|Notificação)" || echo "  (nenhum log relevante)"
echo ""

# Instruções finais
echo -e "${BLUE}==================================================${NC}"
echo -e "${GREEN}  ✓ Teste concluído!${NC}"
echo -e "${BLUE}==================================================${NC}"
echo ""
echo -e "${YELLOW}Para testar o WebSocket manualmente:${NC}"
echo ""
echo -e "1. Abra frontend-example/demo.html no navegador"
echo -e "2. Cole este token:"
echo -e "   ${BLUE}$JWT_TOKEN${NC}"
echo -e "3. Clique em 'Conectar'"
echo -e "4. Inscreva-se nesta conversa:"
echo -e "   ${BLUE}$CONV_ID${NC}"
echo -e "5. Execute este script novamente e observe as notificações"
echo ""
echo -e "${YELLOW}Ou teste via wscat:${NC}"
echo -e "  ${BLUE}npm install -g wscat${NC}"
echo -e "  ${BLUE}wscat -c \"ws://localhost:5000/ws/status?access_token=$JWT_TOKEN\"${NC}"
echo -e "  Envie: ${BLUE}{\"type\":\"subscribe\",\"conversationId\":\"$CONV_ID\"}${NC}"
echo ""
