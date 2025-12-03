#!/bin/bash

# Script de Setup - Semana 1
# Inicializa infraestrutura e prepara ambiente

set -e

echo "=========================================="
echo "Chat App - Setup Semana 1"
echo "=========================================="
echo ""

# Cores para output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# ============================================
# 1. Verificar pré-requisitos
# ============================================
echo -e "${BLUE}[1/5] Verificando pré-requisitos...${NC}"

if ! command -v docker &> /dev/null; then
    echo -e "${RED}? Docker não encontrado${NC}"
    echo "Instale: https://docs.docker.com/get-docker/"
    exit 1
fi
echo -e "${GREEN}? Docker instalado${NC}"

if ! command -v docker-compose &> /dev/null; then
    echo -e "${RED}? Docker Compose não encontrado${NC}"
    exit 1
fi
echo -e "${GREEN}? Docker Compose instalado${NC}"

if ! command -v dotnet &> /dev/null; then
    echo -e "${RED}? .NET SDK não encontrado${NC}"
    echo "Instale: https://dotnet.microsoft.com/download"
    exit 1
fi
echo -e "${GREEN}? .NET SDK $(dotnet --version) instalado${NC}"

echo ""

# ============================================
# 2. Subir infraestrutura
# ============================================
echo -e "${BLUE}[2/5] Subindo infraestrutura (Redis, Kafka, Cassandra)...${NC}"

docker-compose -f docker-compose.dev.yml up -d

echo -e "${GREEN}? Containers iniciados${NC}"
echo ""

# ============================================
# 3. Aguardar serviços ficarem prontos
# ============================================
echo -e "${BLUE}[3/5] Aguardando serviços ficarem prontos...${NC}"

echo "Aguardando Redis..."
timeout 30 bash -c 'until docker exec chat-redis redis-cli ping 2>/dev/null; do sleep 1; done' || {
    echo -e "${RED}? Redis não respondeu no timeout${NC}"
    exit 1
}
echo -e "${GREEN}? Redis pronto${NC}"

echo "Aguardando Kafka (pode demorar ~60s)..."
timeout 120 bash -c 'until docker exec chat-kafka kafka-broker-api-versions --bootstrap-server localhost:9092 2>/dev/null; do sleep 2; done' || {
    echo -e "${RED}? Kafka não respondeu no timeout${NC}"
    exit 1
}
echo -e "${GREEN}? Kafka pronto${NC}"

echo "Aguardando Cassandra (pode demorar ~90s)..."
timeout 180 bash -c 'until docker exec chat-cassandra cqlsh -e "describe cluster" 2>/dev/null; do sleep 3; done' || {
    echo -e "${RED}? Cassandra não respondeu no timeout${NC}"
    exit 1
}
echo -e "${GREEN}? Cassandra pronto${NC}"

echo ""

# ============================================
# 4. Criar keyspace Cassandra
# ============================================
echo -e "${BLUE}[4/5] Criando keyspace no Cassandra...${NC}"

docker exec chat-cassandra cqlsh -e "
CREATE KEYSPACE IF NOT EXISTS chat 
WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};
" 2>/dev/null || {
    echo -e "${YELLOW}??  Keyspace pode já existir${NC}"
}

echo -e "${GREEN}? Keyspace 'chat' criado/verificado${NC}"
echo ""

# ============================================
# 5. Criar tópicos Kafka
# ============================================
echo -e "${BLUE}[5/5] Criando tópicos Kafka...${NC}"

docker exec chat-kafka kafka-topics --create \
    --if-not-exists \
    --bootstrap-server localhost:9092 \
    --topic chat.messages \
    --partitions 3 \
    --replication-factor 1 \
    2>/dev/null || {
    echo -e "${YELLOW}??  Tópico pode já existir${NC}"
}

echo -e "${GREEN}? Tópico 'chat.messages' criado/verificado${NC}"
echo ""

# ============================================
# Resumo
# ============================================
echo -e "${GREEN}=========================================="
echo "? Setup concluído com sucesso!"
echo "==========================================${NC}"
echo ""
echo "Serviços disponíveis:"
echo "  - Redis:        localhost:6379"
echo "  - Kafka:        localhost:9093"
echo "  - Cassandra:    localhost:9042"
echo "  - Kafka UI:     http://localhost:8090"
echo ""
echo "Próximos passos:"
echo "  1. Rodar Chat.Frontend:   cd Chat.Frontend && dotnet run"
echo "  2. Rodar Chat.ApiGateway: cd Chat.ApiGateway && dotnet run"
echo "  3. Testar API:            ./test-api.sh"
echo ""
echo "Para ver logs:"
echo "  docker-compose -f docker-compose.dev.yml logs -f"
echo ""
echo "Para parar tudo:"
echo "  docker-compose -f docker-compose.dev.yml down"
echo ""