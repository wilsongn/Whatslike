# start-chat4all.ps1
# Script para iniciar o Chat4All com ordem correta e aguardar serviços

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Chat4All - Iniciando Servicos        " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$composeFile = "docker-compose-with-frontend.yml"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Funcao para aguardar container ficar healthy
function Wait-ContainerHealthy {
    param([string]$container, [int]$timeout = 120)
    
    Write-Host "`nAguardando $container ficar pronto..." -ForegroundColor Yellow
    $elapsed = 0
    $interval = 5
    
    while ($elapsed -lt $timeout) {
        $status = docker inspect --format='{{.State.Health.Status}}' $container 2>$null
        
        if ($status -eq "healthy") {
            Write-Host "$container esta HEALTHY!" -ForegroundColor Green
            return $true
        }
        
        $running = docker inspect --format='{{.State.Running}}' $container 2>$null
        if ($running -ne "true") {
            Write-Host "$container nao esta rodando. Verificando logs..." -ForegroundColor Red
            docker logs $container --tail 10
            return $false
        }
        
        Write-Host "  Status: $status ($elapsed`s / $timeout`s)" -ForegroundColor Gray
        Start-Sleep -Seconds $interval
        $elapsed += $interval
    }
    
    Write-Host "$container nao ficou healthy em $timeout segundos" -ForegroundColor Red
    return $false
}

# Funcao para aguardar container estar rodando
function Wait-ContainerRunning {
    param([string]$container, [int]$timeout = 60)
    
    Write-Host "`nAguardando $container iniciar..." -ForegroundColor Yellow
    $elapsed = 0
    $interval = 3
    
    while ($elapsed -lt $timeout) {
        $running = docker inspect --format='{{.State.Running}}' $container 2>$null
        if ($running -eq "true") {
            Write-Host "$container esta RODANDO!" -ForegroundColor Green
            return $true
        }
        
        Start-Sleep -Seconds $interval
        $elapsed += $interval
    }
    
    Write-Host "$container nao iniciou em $timeout segundos" -ForegroundColor Red
    return $false
}

# 1. Parar servicos anteriores (se existirem)
Write-Host "`n[1/7] Parando servicos anteriores..." -ForegroundColor Cyan
docker-compose -f $composeFile down 2>$null

# 2. Iniciar infraestrutura base
Write-Host "`n[2/7] Iniciando Cassandra, Redis, Zookeeper, MinIO..." -ForegroundColor Cyan
docker-compose -f $composeFile up -d cassandra redis zookeeper minio

# Aguardar Cassandra (demora mais)
if (-not (Wait-ContainerHealthy "cassandra" 180)) {
    Write-Host "ERRO: Cassandra nao iniciou corretamente" -ForegroundColor Red
    exit 1
}

# Aguardar Redis
if (-not (Wait-ContainerHealthy "redis" 30)) {
    Write-Host "ERRO: Redis nao iniciou corretamente" -ForegroundColor Red
    exit 1
}

# Aguardar Zookeeper
if (-not (Wait-ContainerHealthy "zookeeper" 60)) {
    Write-Host "ERRO: Zookeeper nao iniciou corretamente" -ForegroundColor Red
    exit 1
}

# Aguardar MinIO
if (-not (Wait-ContainerHealthy "minio" 30)) {
    Write-Host "ERRO: MinIO nao iniciou corretamente" -ForegroundColor Red
    exit 1
}

# 3. Inicializar Cassandra (tabelas e dados)
Write-Host "`n[3/7] Inicializando banco de dados Cassandra..." -ForegroundColor Cyan
$initScript = Join-Path $scriptDir "init-cassandra.ps1"
if (Test-Path $initScript) {
    & $initScript
} else {
    Write-Host "Script init-cassandra.ps1 nao encontrado, pulando..." -ForegroundColor Yellow
}

# 4. Iniciar Kafka (depende do Zookeeper)
Write-Host "`n[4/7] Iniciando Kafka..." -ForegroundColor Cyan
docker-compose -f $composeFile up -d kafka

if (-not (Wait-ContainerHealthy "kafka" 120)) {
    Write-Host "ERRO: Kafka nao iniciou corretamente" -ForegroundColor Red
    exit 1
}

# 5. Iniciar Chat-API
Write-Host "`n[5/7] Iniciando Chat-API..." -ForegroundColor Cyan
docker-compose -f $composeFile up -d chat-api

if (-not (Wait-ContainerRunning "chat-api" 60)) {
    Write-Host "ERRO: Chat-API nao iniciou" -ForegroundColor Red
    docker logs chat-api --tail 20
    exit 1
}

# Dar tempo para API inicializar completamente
Write-Host "  Aguardando API inicializar..." -ForegroundColor Gray
Start-Sleep -Seconds 10

# 6. Iniciar Workers e Conectores
Write-Host "`n[6/7] Iniciando Workers e Conectores..." -ForegroundColor Cyan
docker-compose -f $composeFile up -d router-worker status-worker connector-whatsapp connector-instagram

Start-Sleep -Seconds 5

# 7. Iniciar Frontend e Monitoramento
Write-Host "`n[7/7] Iniciando Frontend e Monitoramento..." -ForegroundColor Cyan
docker-compose -f $composeFile up -d chat-frontend prometheus grafana

Start-Sleep -Seconds 5

# Resumo final
Write-Host "`n========================================" -ForegroundColor Green
Write-Host "  Chat4All - Todos os servicos iniciados!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

Write-Host "`nServicos disponiveis:" -ForegroundColor Cyan
Write-Host "  - Chat API:     http://localhost:5000" -ForegroundColor White
Write-Host "  - Frontend:     http://localhost:8080" -ForegroundColor White
Write-Host "  - MinIO:        http://localhost:9001 (admin/minioadmin)" -ForegroundColor White
Write-Host "  - Grafana:      http://localhost:3000 (admin/admin)" -ForegroundColor White
Write-Host "  - Prometheus:   http://localhost:9090" -ForegroundColor White

Write-Host "`nPara ver logs:" -ForegroundColor Yellow
Write-Host "  docker-compose -f $composeFile logs -f chat-api" -ForegroundColor Gray

Write-Host "`nPara parar tudo:" -ForegroundColor Yellow
Write-Host "  docker-compose -f $composeFile down" -ForegroundColor Gray

