# Criar arquivo: corrigir-backend-url.ps1
$file = "docker-compose-with-frontend.yml"
$content = Get-Content $file -Raw

# Procurar seção chat-frontend e adicionar variável se não existir
if ($content -notmatch "BACKEND_WEBSOCKET_URL") {
    # Adicionar após ASPNETCORE_URLS
    $content = $content -replace '(chat-frontend:.*?environment:.*?- ASPNETCORE_URLS=http://\+:8080)', "`$1`n      - BACKEND_WEBSOCKET_URL=ws://chat-api:5000/ws/status"
    $content | Out-File -FilePath $file -Encoding UTF8 -NoNewline
    Write-Host "✅ BACKEND_WEBSOCKET_URL adicionado!"
} else {
    Write-Host "⚠️ BACKEND_WEBSOCKET_URL já existe, verifique se está correto"
}