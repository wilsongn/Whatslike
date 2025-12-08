# generate-tokens.ps1
# Gera tokens JWT válidos para usuários do Chat4All
# 
# Uso:
#   .\generate-tokens.ps1                           # Mostra tokens dos usuários padrão
#   .\generate-tokens.ps1 -New                      # Gera token com novo UUID
#   .\generate-tokens.ps1 -UserId "guid"            # Gera token para UUID específico

param(
    [switch]$New,
    [string]$UserId
)

$secret = "26c8d9a793975af4999bc048990f6fd1"
$orgId = "5a234c5d-fa21-4c30-9a22-d4eaf0beb0be"

function New-Jwt {
    param(
        [string]$UserId,
        [string]$Secret,
        [string]$OrgId
    )
    
    # Header
    $header = @{
        alg = "HS256"
        typ = "JWT"
    } | ConvertTo-Json -Compress
    
    # Payload - expira em 1 ano
    $now = [int][double]::Parse((Get-Date -UFormat %s))
    $exp = $now + 31536000
    
    $payload = @{
        sub = $UserId
        nameid = $UserId
        tenant_id = $OrgId
        aud = "Whatslike.Clients"
        iss = "Whatslike"
        iat = $now
        exp = $exp
    } | ConvertTo-Json -Compress
    
    # Base64Url encode
    $headerB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($header)) -replace '\+','-' -replace '/','_' -replace '='
    $payloadB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload)) -replace '\+','-' -replace '/','_' -replace '='
    
    # Signature
    $toSign = "$headerB64.$payloadB64"
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [Text.Encoding]::UTF8.GetBytes($Secret)
    $sig = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($toSign))
    $sigB64 = [Convert]::ToBase64String($sig) -replace '\+','-' -replace '/','_' -replace '='
    
    return "$headerB64.$payloadB64.$sigB64"
}

# ========== MAIN ==========

if ($New) {
    # Gerar novo token com UUID novo
    $newId = [guid]::NewGuid().ToString()
    Write-Host "User ID: $newId" -ForegroundColor Yellow
    Write-Host ""
    $token = New-Jwt -UserId $newId -Secret $secret -OrgId $orgId
    Write-Host $token -ForegroundColor Green
    
} elseif ($UserId) {
    # Gerar token para UUID específico
    Write-Host "User ID: $UserId" -ForegroundColor Yellow
    Write-Host ""
    $token = New-Jwt -UserId $UserId -Secret $secret -OrgId $orgId
    Write-Host $token -ForegroundColor Green
    
} else {
    # Mostrar tokens dos usuários padrão
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "  Tokens JWT - Chat4All                " -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host ""
    
    Write-Host "Usuario 1 (usuario1):" -ForegroundColor Yellow
    Write-Host "  ID: d59c17c8-d785-4104-935c-94c3ce01883d" -ForegroundColor Gray
    $token1 = New-Jwt -UserId "d59c17c8-d785-4104-935c-94c3ce01883d" -Secret $secret -OrgId $orgId
    Write-Host $token1 -ForegroundColor Green
    Write-Host ""
    
    Write-Host "Usuario 2 (usuario2):" -ForegroundColor Yellow
    Write-Host "  ID: e1b325ff-f351-4477-a572-cccc3d2ea7f8" -ForegroundColor Gray
    $token2 = New-Jwt -UserId "e1b325ff-f351-4477-a572-cccc3d2ea7f8" -Secret $secret -OrgId $orgId
    Write-Host $token2 -ForegroundColor Green
    Write-Host ""
    
    Write-Host "Wilson:" -ForegroundColor Yellow
    Write-Host "  ID: aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" -ForegroundColor Gray
    $token3 = New-Jwt -UserId "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" -Secret $secret -OrgId $orgId
    Write-Host $token3 -ForegroundColor Green
    Write-Host ""
    
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Para gerar novo token: .\generate-tokens.ps1 -New" -ForegroundColor Gray
}

