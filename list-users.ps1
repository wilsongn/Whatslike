# list-users.ps1
# Lista todos os usuários do Cassandra e gera tokens JWT

$secret = "26c8d9a793975af4999bc048990f6fd1"
$orgId = "5a234c5d-fa21-4c30-9a22-d4eaf0beb0be"

function New-Jwt {
    param([string]$UserId)
    
    $header = @{ alg = "HS256"; typ = "JWT" } | ConvertTo-Json -Compress
    $now = [int][double]::Parse((Get-Date -UFormat %s))
    $exp = $now + 31536000
    
    $payload = @{
        sub = $UserId
        nameid = $UserId
        tenant_id = $orgId
        aud = "Whatslike.Clients"
        iss = "Whatslike"
        iat = $now
        exp = $exp
    } | ConvertTo-Json -Compress
    
    $headerB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($header)) -replace '\+','-' -replace '/','_' -replace '='
    $payloadB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload)) -replace '\+','-' -replace '/','_' -replace '='
    
    $hmac = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = [Text.Encoding]::UTF8.GetBytes($secret)
    $sig = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$headerB64.$payloadB64"))
    $sigB64 = [Convert]::ToBase64String($sig) -replace '\+','-' -replace '/','_' -replace '='
    
    return "$headerB64.$payloadB64.$sigB64"
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Usuarios e Tokens - Chat4All         " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Buscar usuários do Cassandra
$output = docker exec cassandra cqlsh -e "SELECT user_id, username, display_name FROM chat.users;" 2>&1

# Parse da saída
$lines = $output -split "`n" | Where-Object { $_ -match "^[\s]*[a-f0-9]{8}-" }

if ($lines.Count -eq 0) {
    Write-Host "Nenhum usuario encontrado" -ForegroundColor Red
    exit
}

$count = 0
foreach ($line in $lines) {
    # Extrair UUID (primeiro campo)
    if ($line -match "([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})") {
        $userId = $matches[1]
        
        # Extrair username e display_name
        $parts = $line -split "\|" | ForEach-Object { $_.Trim() }
        if ($parts.Count -ge 3) {
            $username = $parts[1]
            $displayName = $parts[2]
        } else {
            # Tentar split por espaços múltiplos
            $parts = $line.Trim() -split "\s{2,}"
            $username = if ($parts.Count -ge 2) { $parts[1] } else { "?" }
            $displayName = if ($parts.Count -ge 3) { $parts[2] } else { "?" }
        }
        
        $count++
        Write-Host "[$count] $displayName ($username)" -ForegroundColor Yellow
        Write-Host "    ID: $userId" -ForegroundColor Gray
        $token = New-Jwt -UserId $userId
        Write-Host "    Token: " -NoNewline -ForegroundColor White
        Write-Host $token -ForegroundColor Green
        Write-Host ""
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Total: $count usuarios" -ForegroundColor White
