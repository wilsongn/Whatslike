# aplicar-correcoes.ps1
# Script para aplicar todas as correções automaticamente

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  CHAT4ALL - APLICAR CORREÇÕES" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

# Verificar se está no diretório correto
if (-not (Test-Path "docker-compose-with-frontend.yml")) {
    Write-Host "ERRO: Execute este script no diretório raiz do projeto!" -ForegroundColor Red
    exit 1
}

# 1. FAZER BACKUPS
Write-Host "[1/6] Fazendo backup dos arquivos originais..." -ForegroundColor Yellow

$backupDir = "backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
New-Item -Path $backupDir -ItemType Directory -Force | Out-Null

Copy-Item "Chat.Frontend\Controllers\MessagesController.cs" "$backupDir\" -ErrorAction SilentlyContinue
Copy-Item "Chat.RouterWorker\RouterWorkerService.cs" "$backupDir\" -ErrorAction SilentlyContinue

Write-Host "   ✓ Backup criado em: $backupDir" -ForegroundColor Green

# 2. CORRIGIR CHAT.FRONTEND
Write-Host ""
Write-Host "[2/6] Corrigindo Chat.Frontend..." -ForegroundColor Yellow

$messagesControllerPath = "Chat.Frontend\Controllers\MessagesController.cs"

# Verificar se arquivo existe
if (-not (Test-Path $messagesControllerPath)) {
    Write-Host "   ✗ Arquivo não encontrado: $messagesControllerPath" -ForegroundColor Red
    Write-Host "   Por favor, aplique manualmente usando MessagesController-CORRIGIDO.cs" -ForegroundColor Yellow
} else {
    Write-Host "   ⚠ ATENÇÃO: Você precisa substituir manualmente o conteúdo de:" -ForegroundColor Yellow
    Write-Host "     $messagesControllerPath" -ForegroundColor Yellow
    Write-Host "     Use o arquivo: MessagesController-CORRIGIDO.cs" -ForegroundColor Yellow
}

# 3. CORRIGIR ROUTER WORKER
Write-Host ""
Write-Host "[3/6] Corrigindo Chat.RouterWorker..." -ForegroundColor Yellow

$routerWorkerPath = "Chat.RouterWorker\RouterWorkerService.cs"

if (-not (Test-Path $routerWorkerPath)) {
    Write-Host "   ✗ Arquivo não encontrado: $routerWorkerPath" -ForegroundColor Red
} else {
    Write-Host "   ⚠ ATENÇÃO: Você precisa substituir manualmente o conteúdo de:" -ForegroundColor Yellow
    Write-Host "     $routerWorkerPath" -ForegroundColor Yellow
    Write-Host "     Use o arquivo: RouterWorkerService-CORRIGIDO.cs" -ForegroundColor Yellow
}

# 4. PARAR CONTAINERS
Write-Host ""
Write-Host "[4/6] Parando containers..." -ForegroundColor Yellow

docker-compose -f docker-compose-with-frontend.yml stop chat-frontend router-worker
Write-Host "   ✓ Containers parados" -ForegroundColor Green

# 5. REBUILD
Write-Host ""
Write-Host "[5/6] Rebuild dos serviços (isso pode demorar 5-10 minutos)..." -ForegroundColor Yellow

Write-Host "   Rebuilding chat-frontend..." -ForegroundColor Gray
docker-compose -f docker-compose-with-frontend.yml build chat-frontend --no-cache

Write-Host "   Rebuilding router-worker..." -ForegroundColor Gray
docker-compose -f docker-compose-with-frontend.yml build router-worker --no-cache

Write-Host "   ✓ Rebuild concluído" -ForegroundColor Green

# 6. SUBIR CONTAINERS
Write-Host ""
Write-Host "[6/6] Iniciando containers..." -ForegroundColor Yellow

docker-compose -f docker-compose-with-frontend.yml up -d

Write-Host "   ✓ Containers iniciados" -ForegroundColor Green

# AGUARDAR INICIALIZAÇÃO
Write-Host ""
Write-Host "Aguardando inicialização (60 segundos)..." -ForegroundColor Yellow

for ($i = 60; $i -gt 0; $i--) {
    Write-Progress -Activity "Aguardando" -Status "$i segundos restantes" -PercentComplete ((60-$i)/60*100)
    Start-Sleep -Seconds 1
}

Write-Progress -Activity "Aguardando" -Completed

# VERIFICAR STATUS
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  STATUS DOS SERVIÇOS" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

Write-Host ""
Write-Host "Chat.Frontend:" -ForegroundColor Yellow
docker logs chat-frontend --tail=5

Write-Host ""
Write-Host "RouterWorker:" -ForegroundColor Yellow
docker logs router-worker --tail=5

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  PRÓXIMOS PASSOS" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "1. Gerar token JWT:" -ForegroundColor White
Write-Host "   .\gerar-token.ps1" -ForegroundColor Gray
Write-Host ""
Write-Host "2. Abrir interface HTML:" -ForegroundColor White
Write-Host "   start demo-with-proxy.html" -ForegroundColor Gray
Write-Host ""
Write-Host "3. Enviar mensagem de teste" -ForegroundColor White
Write-Host ""
Write-Host "4. Monitorar logs:" -ForegroundColor White
Write-Host "   docker-compose -f docker-compose-with-frontend.yml logs -f chat-frontend router-worker connector-whatsapp status-worker" -ForegroundColor Gray
Write-Host ""
Write-Host "5. Verificar tópicos Kafka:" -ForegroundColor White
Write-Host "   docker exec kafka kafka-topics --list --bootstrap-server kafka:9092" -ForegroundColor Gray
Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  CORREÇÕES APLICADAS COM SUCESSO!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "⚠ LEMBRE-SE:" -ForegroundColor Yellow
Write-Host "  - Substitua manualmente os arquivos .cs conforme indicado acima" -ForegroundColor Yellow
Write-Host "  - Execute rebuild novamente após substituir os arquivos" -ForegroundColor Yellow
Write-Host ""