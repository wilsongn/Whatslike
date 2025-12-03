# Script de Setup - Semana 1 (Windows PowerShell)
# Inicializa infraestrutura e prepara ambiente

Write-Host "==========================================" -ForegroundColor Blue
Write-Host "Chat App - Setup Semana 1 (Windows)" -ForegroundColor Blue
Write-Host "==========================================" -ForegroundColor Blue
Write-Host ""

# ============================================
# 1. Verificar pré-requisitos
# ============================================
Write-Host "[1/5] Verificando pré-requisitos..." -ForegroundColor Cyan

# Docker
if (!(Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "❌ Docker não encontrado" -ForegroundColor Red
    Write-Host "Instale: https://docs.docker.com/desktop/install/windows-install/" -ForegroundColor Yellow
    exit 1
}
Write-Host "✅ Docker instalado" -ForegroundColor Green

# Docker Compose
if (!(Get-Command docker-compose -ErrorAction SilentlyContinue)) {
    Write-Host "❌ Docker Compose não encontrado" -ForegroundColor Red
    exit 1
}
Write-Host "✅ Docker Compose instalado" -ForegroundColor Green

# .NET SDK
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "❌ .NET SDK não encontrado" -ForegroundColor Red
    Write-Host "Instale: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    exit 1
}
$dotnetVersion = dotnet --version
Write-Host "✅ .NET SDK $dotnetVersion instalado" -ForegroundColor Green

Write-Host ""

# ============================================
# 2. Subir infraestrutura
# ============================================
Write-Host "[2/5] Subindo infraestrutura (Redis, Kafka, Cassandra)..." -ForegroundColor Cyan

docker-compose -f docker-compose.dev.yml up -d

Write-Host "✅ Containers iniciados" -ForegroundColor Green
Write-Host ""

# ============================================
# 3. Aguardar serviços ficarem prontos
# ============================================
Write-Host "[3/5] Aguardando serviços ficarem prontos..." -ForegroundColor Cyan

# Aguardar Redis
Write-Host "Aguardando Redis..." -ForegroundColor Yellow
$timeout = 30
$elapsed = 0
while ($elapsed -lt $timeout) {
    try {
        $result = docker exec chat-redis redis-cli ping 2>&1
        if ($result -match "PONG") {
            Write-Host "✅ Redis pronto" -ForegroundColor Green
            break
        }
    } catch {}
    Start-Sleep -Seconds 1
    $elapsed++
}
if ($elapsed -ge $timeout) {
    Write-Host "❌ Redis não respondeu no timeout" -ForegroundColor Red
    exit 1
}

# Aguardar Kafka
Write-Host "Aguardando Kafka (pode demorar ~60s)..." -ForegroundColor Yellow
$timeout = 120
$elapsed = 0
while ($elapsed -lt $timeout) {
    try {
        $result = docker exec chat-kafka kafka-broker-api-versions --bootstrap-server localhost:9092 2>&1
        if ($result -match "ApiVersion") {
            Write-Host "✅ Kafka pronto" -ForegroundColor Green
            break
        }
    } catch {}
    Start-Sleep -Seconds 2
    $elapsed += 2
}
if ($elapsed -ge $timeout) {
    Write-Host "❌ Kafka não respondeu no timeout" -ForegroundColor Red
    exit 1
}

# Aguardar Cassandra
Write-Host "Aguardando Cassandra (pode demorar ~90s)..." -ForegroundColor Yellow
$timeout = 180
$elapsed = 0
while ($elapsed -lt $timeout) {
    try {
        $result = docker exec chat-cassandra cqlsh -e "describe cluster" 2>&1
        if ($result -match "Cluster") {
            Write-Host "✅ Cassandra pronto" -ForegroundColor Green
            break
        }
    } catch {}
    Start-Sleep -Seconds 3
    $elapsed += 3
}
if ($elapsed -ge $timeout) {
    Write-Host "❌ Cassandra não respondeu no timeout" -ForegroundColor Red
    exit 1
}

Write-Host ""

# ============================================
# 4. Criar keyspace Cassandra
# ============================================
Write-Host "[4/5] Criando keyspace no Cassandra..." -ForegroundColor Cyan

try {
    docker exec chat-cassandra cqlsh -e "CREATE KEYSPACE IF NOT EXISTS chat WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};" 2>&1 | Out-Null
    Write-Host "✅ Keyspace 'chat' criado/verificado" -ForegroundColor Green
} catch {
    Write-Host "⚠️  Keyspace pode já existir" -ForegroundColor Yellow
}

Write-Host ""

# ============================================
# 5. Criar tópicos Kafka
# ============================================
Write-Host "[5/5] Criando tópicos Kafka..." -ForegroundColor Cyan

try {
    docker exec chat-kafka kafka-topics --create --if-not-exists --bootstrap-server localhost:9092 --topic chat.messages --partitions 3 --replication-factor 1 2>&1 | Out-Null
    Write-Host "✅ Tópico 'chat.messages' criado/verificado" -ForegroundColor Green
} catch {
    Write-Host "⚠️  Tópico pode já existir" -ForegroundColor Yellow
}

Write-Host ""

# ============================================
# Resumo
# ============================================
Write-Host "==========================================" -ForegroundColor Green
Write-Host "✅ Setup concluído com sucesso!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Serviços disponíveis:" -ForegroundColor White
Write-Host "  - Redis:        localhost:6379"
Write-Host "  - Kafka:        localhost:9093"
Write-Host "  - Cassandra:    localhost:9042"
Write-Host "  - Kafka UI:     http://localhost:8090"
Write-Host ""
Write-Host "Próximos passos:" -ForegroundColor Yellow
Write-Host "  1. Abrir novo terminal PowerShell"
Write-Host "  2. Rodar Chat.Frontend:"
Write-Host "     cd Chat.Frontend"
Write-Host "     dotnet run"
Write-Host ""
Write-Host "  3. Abrir outro terminal PowerShell"
Write-Host "  4. Rodar Chat.ApiGateway:"
Write-Host "     cd Chat.ApiGateway"
Write-Host "     dotnet run"
Write-Host ""
Write-Host "  5. Testar API:"
Write-Host "     .\test-api.ps1"
Write-Host ""
Write-Host "Para ver logs:" -ForegroundColor Cyan
Write-Host "  docker-compose -f docker-compose.dev.yml logs -f"
Write-Host ""
Write-Host "Para parar tudo:" -ForegroundColor Cyan
Write-Host "  docker-compose -f docker-compose.dev.yml down"
Write-Host ""