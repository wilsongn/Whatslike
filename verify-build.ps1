# Script de Verificação Rápida (Windows PowerShell)
# Verifica se os projetos compilam corretamente

Write-Host "==========================================" -ForegroundColor Blue
Write-Host "Verificação de Build - Semana 1 (Windows)" -ForegroundColor Blue
Write-Host "==========================================" -ForegroundColor Blue
Write-Host ""

# ============================================
# 1. Verificar .NET SDK
# ============================================
Write-Host "[1/5] Verificando .NET SDK..." -ForegroundColor Cyan

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "❌ .NET SDK não encontrado" -ForegroundColor Red
    Write-Host "Instale: https://dotnet.microsoft.com/download" -ForegroundColor Yellow
    exit 1
}

$DOTNET_VERSION = dotnet --version
Write-Host "✅ .NET SDK $DOTNET_VERSION" -ForegroundColor Green
Write-Host ""

# ============================================
# 2. Limpar builds anteriores
# ============================================
Write-Host "[2/5] Limpando builds anteriores..." -ForegroundColor Cyan

if (Test-Path "Chat.Frontend") {
    Push-Location Chat.Frontend
    dotnet clean | Out-Null
    if (Test-Path "bin") { Remove-Item -Recurse -Force bin }
    if (Test-Path "obj") { Remove-Item -Recurse -Force obj }
    Pop-Location
    Write-Host "✅ Chat.Frontend limpo" -ForegroundColor Green
} else {
    Write-Host "⚠️  Chat.Frontend não encontrado" -ForegroundColor Yellow
}

if (Test-Path "Chat.ApiGateway") {
    Push-Location Chat.ApiGateway
    dotnet clean | Out-Null
    if (Test-Path "bin") { Remove-Item -Recurse -Force bin }
    if (Test-Path "obj") { Remove-Item -Recurse -Force obj }
    Pop-Location
    Write-Host "✅ Chat.ApiGateway limpo" -ForegroundColor Green
} else {
    Write-Host "⚠️  Chat.ApiGateway não encontrado" -ForegroundColor Yellow
}

Write-Host ""

# ============================================
# 3. Restore Chat.Frontend
# ============================================
Write-Host "[3/5] Restaurando Chat.Frontend..." -ForegroundColor Cyan

if (Test-Path "Chat.Frontend") {
    Push-Location Chat.Frontend
    
    $output = dotnet restore 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Erro ao restaurar Chat.Frontend" -ForegroundColor Red
        Write-Host $output
        Pop-Location
        exit 1
    }
    
    Pop-Location
    Write-Host "✅ Chat.Frontend restaurado" -ForegroundColor Green
} else {
    Write-Host "❌ Chat.Frontend não encontrado" -ForegroundColor Red
    exit 1
}

Write-Host ""

# ============================================
# 4. Restore Chat.ApiGateway
# ============================================
Write-Host "[4/5] Restaurando Chat.ApiGateway..." -ForegroundColor Cyan

if (Test-Path "Chat.ApiGateway") {
    Push-Location Chat.ApiGateway
    
    $output = dotnet restore 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host "❌ Erro ao restaurar Chat.ApiGateway" -ForegroundColor Red
        Write-Host $output
        Pop-Location
        exit 1
    }
    
    Pop-Location
    Write-Host "✅ Chat.ApiGateway restaurado" -ForegroundColor Green
} else {
    Write-Host "❌ Chat.ApiGateway não encontrado" -ForegroundColor Red
    exit 1
}

Write-Host ""

# ============================================
# 5. Build ambos os projetos
# ============================================
Write-Host "[5/5] Compilando projetos..." -ForegroundColor Cyan

# Build Chat.Frontend
Write-Host "Compilando Chat.Frontend..." -ForegroundColor Yellow
$output = dotnet build Chat.Frontend\Chat.Frontend.csproj 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Erro ao compilar Chat.Frontend" -ForegroundColor Red
    Write-Host ""
    Write-Host "Últimas linhas do log:" -ForegroundColor Yellow
    $output | Select-Object -Last 20 | Write-Host
    exit 1
}
Write-Host "✅ Chat.Frontend compilado" -ForegroundColor Green

# Build Chat.ApiGateway
Write-Host "Compilando Chat.ApiGateway..." -ForegroundColor Yellow
$output = dotnet build Chat.ApiGateway\Chat.ApiGateway.csproj 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "❌ Erro ao compilar Chat.ApiGateway" -ForegroundColor Red
    Write-Host ""
    Write-Host "Últimas linhas do log:" -ForegroundColor Yellow
    $output | Select-Object -Last 20 | Write-Host
    exit 1
}
Write-Host "✅ Chat.ApiGateway compilado" -ForegroundColor Green

Write-Host ""

# ============================================
# Resumo
# ============================================
Write-Host "==========================================" -ForegroundColor Green
Write-Host "✅ Verificação concluída com sucesso!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Todos os projetos compilaram sem erros!" -ForegroundColor White
Write-Host ""
Write-Host "Próximos passos:" -ForegroundColor Yellow
Write-Host "  1. Execute: .\setup.ps1"
Write-Host "  2. Abra novo terminal PowerShell e rode:"
Write-Host "     cd Chat.Frontend"
Write-Host "     dotnet run"
Write-Host "  3. Abra outro terminal PowerShell e rode:"
Write-Host "     cd Chat.ApiGateway"
Write-Host "     dotnet run"
Write-Host "  4. Teste: .\test-api.ps1"
Write-Host ""
Write-Host "Endpoints:" -ForegroundColor Cyan
Write-Host "  - Frontend:  http://localhost:8080"
Write-Host "  - Gateway:   http://localhost:8000"
Write-Host "  - Swagger:   http://localhost:8080/swagger"
Write-Host ""