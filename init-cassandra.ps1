# init-cassandra.ps1
# Script para inicializar o Cassandra com tabelas e dados seed

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Inicializando Cassandra              " -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# 1. Aguardar Cassandra estar pronto
Write-Host "`n[1/6] Aguardando Cassandra estar pronto..." -ForegroundColor Yellow
$maxRetries = 30
$retry = 0
while ($retry -lt $maxRetries) {
    $result = docker exec cassandra cqlsh -e "SELECT now() FROM system.local;" 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Cassandra esta pronto!" -ForegroundColor Green
        break
    }
    $retry++
    Write-Host "  Tentativa $retry/$maxRetries..." -ForegroundColor Gray
    Start-Sleep -Seconds 5
}

if ($retry -eq $maxRetries) {
    Write-Host "ERRO: Cassandra nao ficou pronto" -ForegroundColor Red
    exit 1
}

# 2. Criar keyspace chat se não existir
Write-Host "`n[2/6] Criando keyspace chat..." -ForegroundColor Yellow
docker exec cassandra cqlsh -e "CREATE KEYSPACE IF NOT EXISTS chat WITH replication = {'class': 'SimpleStrategy', 'replication_factor': 1};"
Write-Host "Keyspace chat criado/verificado" -ForegroundColor Green

# 3. Criar tabelas principais
Write-Host "`n[3/6] Criando tabelas..." -ForegroundColor Yellow

$createTables = @"
-- Usuarios
CREATE TABLE IF NOT EXISTS chat.users (
    user_id uuid PRIMARY KEY,
    organization_id uuid,
    username text,
    display_name text,
    email text,
    avatar_url text,
    status text,
    created_at timestamp,
    updated_at timestamp
);

CREATE TABLE IF NOT EXISTS chat.users_by_username (
    organization_id uuid,
    username text,
    user_id uuid,
    PRIMARY KEY ((organization_id), username)
);

CREATE TABLE IF NOT EXISTS chat.users_by_email (
    organization_id uuid,
    email text,
    user_id uuid,
    PRIMARY KEY ((organization_id), email)
);

-- Conversas
CREATE TABLE IF NOT EXISTS chat.conversations (
    conversation_id uuid PRIMARY KEY,
    organization_id uuid,
    type text,
    name text,
    description text,
    avatar_url text,
    created_by uuid,
    created_at timestamp,
    updated_at timestamp
);

CREATE TABLE IF NOT EXISTS chat.conversation_members (
    conversation_id uuid,
    user_id uuid,
    role text,
    joined_at timestamp,
    added_by uuid,
    PRIMARY KEY ((conversation_id), user_id)
);

CREATE TABLE IF NOT EXISTS chat.user_conversations (
    user_id uuid,
    last_message_at timestamp,
    conversation_id uuid,
    type text,
    name text,
    avatar_url text,
    last_message_preview text,
    last_message_sender text,
    unread_count int,
    is_muted boolean,
    is_pinned boolean,
    PRIMARY KEY ((user_id), last_message_at, conversation_id)
) WITH CLUSTERING ORDER BY (last_message_at DESC, conversation_id ASC);

CREATE TABLE IF NOT EXISTS chat.private_conversations (
    organization_id uuid,
    user_pair text,
    conversation_id uuid,
    PRIMARY KEY ((organization_id), user_pair)
);

-- Presenca
CREATE TABLE IF NOT EXISTS chat.user_presence (
    user_id uuid PRIMARY KEY,
    status text,
    last_seen timestamp,
    device_info text
);

CREATE TABLE IF NOT EXISTS chat.presence_history (
    user_id uuid,
    changed_at timestamp,
    status text,
    PRIMARY KEY ((user_id), changed_at)
) WITH CLUSTERING ORDER BY (changed_at DESC);

-- Mensagens pendentes
CREATE TABLE IF NOT EXISTS chat.pending_messages (
    user_id uuid,
    created_at timestamp,
    message_id uuid,
    conversation_id uuid,
    sender_id uuid,
    content text,
    content_type text,
    PRIMARY KEY ((user_id), created_at, message_id)
) WITH CLUSTERING ORDER BY (created_at ASC, message_id ASC)
   AND default_time_to_live = 2592000;

CREATE TABLE IF NOT EXISTS chat.pending_messages_count (
    user_id uuid PRIMARY KEY,
    count counter
);

-- Mensagens (se nao existir)
CREATE TABLE IF NOT EXISTS chat.mensagens (
    organizacao_id uuid,
    conversa_id uuid,
    bucket int,
    sequencia bigint,
    conteudo text,
    criado_em timestamp,
    direcao text,
    id_msg_provedor text,
    mensagem_id uuid,
    status text,
    usuario_remetente_id uuid,
    status_ts map<text, timestamp>,
    PRIMARY KEY ((organizacao_id, conversa_id, bucket), sequencia)
) WITH CLUSTERING ORDER BY (sequencia ASC);

-- Sequencia de conversa
CREATE TABLE IF NOT EXISTS chat.sequencia_conversa (
    organizacao_id uuid,
    conversa_id uuid,
    bucket int,
    proxima_sequencia bigint,
    PRIMARY KEY ((organizacao_id, conversa_id, bucket))
);
"@

# Salvar em arquivo temporário e executar
$createTables | Out-File -FilePath ".\temp_create_tables.cql" -Encoding UTF8
docker cp ".\temp_create_tables.cql" cassandra:/tmp/create_tables.cql
docker exec cassandra cqlsh -f /tmp/create_tables.cql
Remove-Item ".\temp_create_tables.cql" -ErrorAction SilentlyContinue
Write-Host "Tabelas criadas/verificadas" -ForegroundColor Green

# 4. Adicionar colunas de arquivo e canal nas mensagens
Write-Host "`n[4/6] Adicionando colunas extras..." -ForegroundColor Yellow
docker exec cassandra cqlsh -e "ALTER TABLE chat.mensagens ADD file_id uuid;" 2>$null
docker exec cassandra cqlsh -e "ALTER TABLE chat.mensagens ADD file_name text;" 2>$null
docker exec cassandra cqlsh -e "ALTER TABLE chat.mensagens ADD file_size bigint;" 2>$null
docker exec cassandra cqlsh -e "ALTER TABLE chat.mensagens ADD file_extension text;" 2>$null
docker exec cassandra cqlsh -e "ALTER TABLE chat.mensagens ADD file_organization_id uuid;" 2>$null
docker exec cassandra cqlsh -e "ALTER TABLE chat.mensagens ADD canal text;" 2>$null
Write-Host "Colunas extras adicionadas" -ForegroundColor Green

# 5. Criar usuarios seed
Write-Host "`n[5/6] Criando usuarios seed..." -ForegroundColor Yellow

$seedUsers = @"
-- Usuario 1
INSERT INTO chat.users (user_id, organization_id, username, display_name, email, status, created_at, updated_at) 
VALUES (d59c17c8-d785-4104-935c-94c3ce01883d, 5a234c5d-fa21-4c30-9a22-d4eaf0beb0be, 'usuario1', 'Usuario Teste 1', 'usuario1@test.com', 'active', toTimestamp(now()), toTimestamp(now()));

INSERT INTO chat.users_by_username (organization_id, username, user_id) 
VALUES (5a234c5d-fa21-4c30-9a22-d4eaf0beb0be, 'usuario1', d59c17c8-d785-4104-935c-94c3ce01883d);

-- Usuario 2
INSERT INTO chat.users (user_id, organization_id, username, display_name, email, status, created_at, updated_at) 
VALUES (e1b325ff-f351-4477-a572-cccc3d2ea7f8, 5a234c5d-fa21-4c30-9a22-d4eaf0beb0be, 'usuario2', 'Usuario Teste 2', 'usuario2@test.com', 'active', toTimestamp(now()), toTimestamp(now()));

INSERT INTO chat.users_by_username (organization_id, username, user_id) 
VALUES (5a234c5d-fa21-4c30-9a22-d4eaf0beb0be, 'usuario2', e1b325ff-f351-4477-a572-cccc3d2ea7f8);

-- Wilson
INSERT INTO chat.users (user_id, organization_id, username, display_name, email, status, created_at, updated_at) 
VALUES (aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee, 5a234c5d-fa21-4c30-9a22-d4eaf0beb0be, 'wilson', 'Wilson', 'wilson@test.com', 'active', toTimestamp(now()), toTimestamp(now()));

INSERT INTO chat.users_by_username (organization_id, username, user_id) 
VALUES (5a234c5d-fa21-4c30-9a22-d4eaf0beb0be, 'wilson', aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee);
"@

$seedUsers | Out-File -FilePath ".\temp_seed_users.cql" -Encoding UTF8
docker cp ".\temp_seed_users.cql" cassandra:/tmp/seed_users.cql
docker exec cassandra cqlsh -f /tmp/seed_users.cql
Remove-Item ".\temp_seed_users.cql" -ErrorAction SilentlyContinue
Write-Host "Usuarios seed criados" -ForegroundColor Green

# 6. Verificar
Write-Host "`n[6/6] Verificando..." -ForegroundColor Yellow
Write-Host "`nTabelas no keyspace chat:" -ForegroundColor Cyan
docker exec cassandra cqlsh -e "USE chat; DESCRIBE TABLES;"

Write-Host "`nUsuarios cadastrados:" -ForegroundColor Cyan
docker exec cassandra cqlsh -e "SELECT user_id, username, display_name FROM chat.users;"

Write-Host "`n========================================" -ForegroundColor Green
Write-Host "  Cassandra inicializado com sucesso!  " -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green

Write-Host "`nTokens JWT para teste:" -ForegroundColor Yellow
Write-Host "Usuario 1 (d59c17c8...):" -ForegroundColor White
Write-Host "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE3NjUxOTE5MzcsInN1YiI6ImQ1OWMxN2M4LWQ3ODUtNDEwNC05MzVjLTk0YzNjZTAxODgzZCIsIm5hbWVpZCI6ImQ1OWMxN2M4LWQ3ODUtNDEwNC05MzVjLTk0YzNjZTAxODgzZCIsInRlbmFudF9pZCI6IjVhMjM0YzVkLWZhMjEtNGMzMC05YTIyLWQ0ZWFmMGJlYjBiZSIsImF1ZCI6IldoYXRzbGlrZS5DbGllbnRzIiwiaXNzIjoiV2hhdHNsaWtlIiwiaWF0IjoxNzY1MTA1NTM3fQ.Hn8jDPPoLLmN8mESOHJjpcbg8xHOMBVGrqS4qMzXwrE" -ForegroundColor Gray

Write-Host "`nUsuario 2 (e1b325ff...):" -ForegroundColor White
Write-Host "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE3NjUxOTE5MzcsInN1YiI6ImUxYjMyNWZmLWYzNTEtNDQ3Ny1hNTcyLWNjY2MzZDJlYTdmOCIsIm5hbWVpZCI6ImUxYjMyNWZmLWYzNTEtNDQ3Ny1hNTcyLWNjY2MzZDJlYTdmOCIsInRlbmFudF9pZCI6IjVhMjM0YzVkLWZhMjEtNGMzMC05YTIyLWQ0ZWFmMGJlYjBiZSIsImF1ZCI6IldoYXRzbGlrZS5DbGllbnRzIiwiaXNzIjoiV2hhdHNsaWtlIiwiaWF0IjoxNzY1MTA1NTM3fQ.xvNpsLCirsokE_22zFdJlqzXWOiEw_6whMRRkMqqMoc" -ForegroundColor Gray

Write-Host "`nWilson (aaaaaaaa...):" -ForegroundColor White
Write-Host "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJleHAiOjE3NjUxOTE5MzcsInN1YiI6ImFhYWFhYWFhLWJiYmItY2NjYy1kZGRkLWVlZWVlZWVlZWVlZSIsIm5hbWVpZCI6ImFhYWFhYWFhLWJiYmItY2NjYy1kZGRkLWVlZWVlZWVlZWVlZSIsInRlbmFudF9pZCI6IjVhMjM0YzVkLWZhMjEtNGMzMC05YTIyLWQ0ZWFmMGJlYjBiZSIsImF1ZCI6IldoYXRzbGlrZS5DbGllbnRzIiwiaXNzIjoiV2hhdHNsaWtlIiwiaWF0IjoxNzY1MTA1NTM3fQ.DBTs9rPXxIbvQrsvQLL2RvcaDXvqsZvVNRT3VlTOOgk" -ForegroundColor Gray