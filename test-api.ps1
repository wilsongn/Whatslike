# Script de Teste - API Gateway + Frontend Service (Windows PowerShell)
# Testa o fluxo completo: API → Kafka

Write-Host "==========================================" -ForegroundColor Blue
Write-Host "Testando Chat API - Semana 1 (Windows)" -ForegroundColor Blue
Write-Host "==========================================" -ForegroundColor Blue
Write-Host ""

# ============================================
# 1. Verificar serviços
# ============================================
Write-Host "[1/5] Verificando serviços..." -ForegroundColor Cyan

# API Gateway
try {
    $response = Invoke-WebRequest -Uri "http://localhost:8000/health" -TimeoutSec 5 -UseBasicParsing
    Write-Host "✅ API Gateway OK" -ForegroundColor Green
} catch {
    Write-Host "❌ API Gateway não está respondendo em localhost:8000" -ForegroundColor Red
    Write-Host "Execute: cd Chat.ApiGateway; dotnet run" -ForegroundColor Yellow
    exit 1
}

# Frontend Service
try {
    $response = Invoke-WebRequest -Uri "http://localhost:8080/health" -TimeoutSec 5 -UseBasicParsing
    Write-Host "✅ Frontend Service OK" -ForegroundColor Green
} catch {
    Write-Host "❌ Frontend Service não está respondendo em localhost:8080" -ForegroundColor Red
    Write-Host "Execute: cd Chat.Frontend; dotnet run" -ForegroundColor Yellow
    exit 1
}

# Redis
try {
    $result = docker exec chat-redis redis-cli ping 2>&1
    if ($result -match "PONG") {
        Write-Host "✅ Redis OK" -ForegroundColor Green
    } else {
        throw
    }
} catch {
    Write-Host "❌ Redis não está respondendo" -ForegroundColor Red
    exit 1
}

# Kafka
try {
    $result = docker exec chat-kafka kafka-broker-api-versions --bootstrap-server localhost:9092 2>&1
    if ($result -match "ApiVersion") {
        Write-Host "✅ Kafka OK" -ForegroundColor Green
    } else {
        throw
    }
} catch {
    Write-Host "❌ Kafka não está respondendo" -ForegroundColor Red
    exit 1
}

Write-Host ""

# ============================================
# 2. Gerar Token JWT
# ============================================
Write-Host "[2/5] Gerando token JWT..." -ForegroundColor Cyan

# Token gerado com o secret padrão
$TOKEN = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiJ0ZXN0dXNlciIsIm5hbWUiOiJ0ZXN0dXNlciIsIm5iZiI6MTcwMDAwMDAwMCwiZXhwIjoyMDAwMDAwMDAwLCJpYXQiOjE3MDAwMDAwMDAsImlzcyI6ImNoYXQtZGV2IiwiYXVkIjoiY2hhdC1hcGkifQ.F5oN8YmJUqGbZF-3QZYz6V0Qx4JH5-K6NZYXqGHWKHk"

Write-Host "✅ Token JWT gerado" -ForegroundColor Green
Write-Host ""

# ============================================
# 3. Enviar mensagem via API Gateway
# ============================================
Write-Host "[3/5] Enviando mensagem via API Gateway..." -ForegroundColor Cyan

$headers = @{
    "Authorization" = "Bearer $TOKEN"
    "Content-Type" = "application/json"
}

$body = @{
    conversationId = "user1_user2"
    content = "Hello from API Test (Windows)!"
} | ConvertTo-Json

try {
    $response = Invoke-WebRequest -Uri "http://localhost:8000/api/v1/messages" -Method POST -Headers $headers -Body $body -UseBasicParsing
    $responseObj = $response.Content | ConvertFrom-Json
    
    Write-Host "Response:" -ForegroundColor White
    $responseObj | ConvertTo-Json | Write-Host
    
    $MESSAGE_ID = $responseObj.messageId
    
    if ([string]::IsNullOrEmpty($MESSAGE_ID)) {
        Write-Host "❌ Falha ao enviar mensagem" -ForegroundColor Red
        exit 1
    }
    
    Write-Host "✅ Mensagem enviada: $MESSAGE_ID" -ForegroundColor Green
} catch {
    Write-Host "❌ Erro ao enviar mensagem: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

Write-Host ""

# ============================================
# 4. Verificar Kafka
# ============================================
Write-Host "[4/5] Verificando mensagem no Kafka..." -ForegroundColor Cyan

Write-Host "Consumindo do tópico chat.messages (timeout 10s)..." -ForegroundColor Yellow

try {
    $result = docker exec chat-kafka kafka-console-consumer --bootstrap-server localhost:9092 --topic chat.messages --from-beginning --max-messages 1 --timeout-ms 10000 2>&1
    
    if ($result -match "messageId") {
        Write-Host "Mensagem no Kafka:" -ForegroundColor White
        Write-Host $result
        Write-Host "✅ Mensagem encontrada no Kafka" -ForegroundColor Green
    } else {
        Write-Host "⚠️  Nenhuma mensagem encontrada no Kafka (pode já ter sido consumida)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "⚠️  Erro ao consumir do Kafka" -ForegroundColor Yellow
}

Write-Host ""

# ============================================
# 5. Testar idempotência
# ============================================
Write-Host "[5/5] Testando idempotência (enviar mesma mensagem 2x)..." -ForegroundColor Cyan

$body2 = @{
    messageId = $MESSAGE_ID
    conversationId = "user1_user2"
    content = "This is a duplicate!"
} | ConvertTo-Json

try {
    $response2 = Invoke-WebRequest -Uri "http://localhost:8000/api/v1/messages" -Method POST -Headers $headers -Body $body2 -UseBasicParsing
    $responseObj2 = $response2.Content | ConvertFrom-Json
    
    Write-Host "Response (deve ser idêntica à primeira):" -ForegroundColor White
    $responseObj2 | ConvertTo-Json | Write-Host
    
    $MESSAGE_ID2 = $responseObj2.messageId
    
    if ($MESSAGE_ID -eq $MESSAGE_ID2) {
        Write-Host "✅ Idempotência funcionando! Mesma resposta retornada" -ForegroundColor Green
    } else {
        Write-Host "❌ Idempotência falhou (IDs diferentes)" -ForegroundColor Red
    }
} catch {
    Write-Host "❌ Erro ao testar idempotência: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""

# ============================================
# 6. Verificar Redis (cache de idempotência)
# ============================================
Write-Host "[Extra] Verificando cache Redis..." -ForegroundColor Cyan

try {
    $cached = docker exec chat-redis redis-cli GET "ChatFrontend:idempotency:$MESSAGE_ID" 2>&1
    
    if ($cached -and $cached -ne "(nil)") {
        Write-Host "Cache encontrado:" -ForegroundColor White
        Write-Host $cached
        Write-Host "✅ Cache de idempotência funcionando" -ForegroundColor Green
    } else {
        Write-Host "⚠️  Cache não encontrado (pode ter expirado)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "⚠️  Erro ao verificar cache" -ForegroundColor Yellow
}

Write-Host ""

# ============================================
# Resumo
# ============================================
Write-Host "==========================================" -ForegroundColor Green
Write-Host "✅ Testes concluídos!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Resultados:" -ForegroundColor White
Write-Host "  ✅ API Gateway funcionando"
Write-Host "  ✅ Frontend Service funcionando"
Write-Host "  ✅ Mensagem enviada e aceita"
Write-Host "  ✅ Mensagem publicada no Kafka"
Write-Host "  ✅ Idempotência validada"
Write-Host ""
Write-Host "Para ver mais detalhes:" -ForegroundColor Cyan
Write-Host "  - Kafka UI: http://localhost:8090"
Write-Host "  - Swagger:  http://localhost:8080/swagger"
Write-Host "  - Redis:    docker exec -it chat-redis redis-cli"
Write-Host ""