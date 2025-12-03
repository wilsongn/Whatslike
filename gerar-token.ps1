# Script para gerar JWT Token válido para testes
# PowerShell - Windows

$ErrorActionPreference = "Stop"

# Configuração
$SECRET = "26c8d9a793975af4999bc048990f6fd1"
$ISSUER = "Whatslike"
$AUDIENCE = "Whatslike.Clients"

# Gerar IDs (UUID)
$USER_ID = [guid]::NewGuid().ToString()
$ORG_ID = [guid]::NewGuid().ToString()

Write-Host "🔑 Gerando JWT Token..." -ForegroundColor Cyan
Write-Host ""
Write-Host "User ID: $USER_ID" -ForegroundColor Yellow
Write-Host "Org ID: $ORG_ID" -ForegroundColor Yellow
Write-Host ""

# Header
$header = @{
    alg = "HS256"
    typ = "JWT"
} | ConvertTo-Json -Compress

$headerBytes = [System.Text.Encoding]::UTF8.GetBytes($header)
$headerEncoded = [Convert]::ToBase64String($headerBytes) -replace '\+','-' -replace '/','_' -replace '='

# Payload
$now = [int][double]::Parse((Get-Date -UFormat %s))
$exp = $now + 86400 # 24 horas

$payload = @{
    sub = $USER_ID
    nameid = $USER_ID
    tenant_id = $ORG_ID
    iss = $ISSUER
    aud = $AUDIENCE
    iat = $now
    exp = $exp
} | ConvertTo-Json -Compress

$payloadBytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
$payloadEncoded = [Convert]::ToBase64String($payloadBytes) -replace '\+','-' -replace '/','_' -replace '='

# Signature
$unsignedToken = "$headerEncoded.$payloadEncoded"
$hmac = New-Object System.Security.Cryptography.HMACSHA256
$hmac.Key = [System.Text.Encoding]::UTF8.GetBytes($SECRET)
$signatureBytes = $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($unsignedToken))
$signatureEncoded = [Convert]::ToBase64String($signatureBytes) -replace '\+','-' -replace '/','_' -replace '='

# Token completo
$TOKEN = "$unsignedToken.$signatureEncoded"

Write-Host "✅ Token gerado com sucesso!" -ForegroundColor Green
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host "TOKEN:" -ForegroundColor Yellow
Write-Host $TOKEN -ForegroundColor White
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Cyan
Write-Host ""
Write-Host "📋 Para usar nos testes, execute:" -ForegroundColor Yellow
Write-Host ""
Write-Host "`$env:JWT_TOKEN = `"$TOKEN`"" -ForegroundColor Green
Write-Host "`$env:USER_ID = `"$USER_ID`"" -ForegroundColor Green
Write-Host "`$env:ORG_ID = `"$ORG_ID`"" -ForegroundColor Green
Write-Host ""
Write-Host "🧪 Teste de autenticação:" -ForegroundColor Yellow
Write-Host "curl http://localhost:8080/health -H `"Authorization: Bearer `$env:JWT_TOKEN`"" -ForegroundColor Cyan
Write-Host ""
Write-Host "📋 Copie e cole os comandos acima no PowerShell!" -ForegroundColor Yellow
Write-Host ""

# Salvar em arquivo para fácil cópia
$tokenFile = "jwt-token.txt"
@"
JWT_TOKEN=$TOKEN
USER_ID=$USER_ID
ORG_ID=$ORG_ID

# Para usar no PowerShell:
`$env:JWT_TOKEN = "$TOKEN"
`$env:USER_ID = "$USER_ID"
`$env:ORG_ID = "$ORG_ID"

# Para usar no CMD:
set JWT_TOKEN=$TOKEN
set USER_ID=$USER_ID
set ORG_ID=$ORG_ID
"@ | Out-File -FilePath $tokenFile -Encoding UTF8

Write-Host "💾 Token salvo em: $tokenFile" -ForegroundColor Green
Write-Host ""