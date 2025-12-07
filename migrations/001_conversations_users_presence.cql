-- ============================================
-- Chat4All v2 - Migration 001
-- Tabelas: Users, Conversations, Presence, Pending Messages
-- ============================================

-- Usar keyspace existente
USE chat;

-- ============================================
-- 1. USUÁRIOS
-- ============================================

-- Tabela principal de usuários
CREATE TABLE IF NOT EXISTS users (
    user_id UUID PRIMARY KEY,
    organization_id UUID,
    username TEXT,
    display_name TEXT,
    email TEXT,
    avatar_url TEXT,
    created_at TIMESTAMP,
    updated_at TIMESTAMP
);

-- Índice para buscar usuário por username dentro de uma organização
-- Permite: "encontre o usuário 'wilson' na organização X"
CREATE TABLE IF NOT EXISTS users_by_username (
    organization_id UUID,
    username TEXT,
    user_id UUID,
    display_name TEXT,
    email TEXT,
    avatar_url TEXT,
    PRIMARY KEY ((organization_id), username)
);

-- Índice para buscar usuário por email
CREATE TABLE IF NOT EXISTS users_by_email (
    email TEXT,
    user_id UUID,
    organization_id UUID,
    username TEXT,
    display_name TEXT,
    PRIMARY KEY ((email))
);

-- ============================================
-- 2. CONVERSAS
-- ============================================

-- Tabela principal de conversas
CREATE TABLE IF NOT EXISTS conversations (
    conversation_id UUID PRIMARY KEY,
    organization_id UUID,
    type TEXT,                    -- 'private' ou 'group'
    name TEXT,                    -- nome do grupo (null para private)
    description TEXT,             -- descrição do grupo
    avatar_url TEXT,              -- foto do grupo
    created_by UUID,              -- user_id do criador
    created_at TIMESTAMP,
    updated_at TIMESTAMP,
    metadata TEXT                 -- JSON com configs extras
);

-- Membros de cada conversa
-- Partition key: conversation_id (para listar todos os membros de uma conversa)
CREATE TABLE IF NOT EXISTS conversation_members (
    conversation_id UUID,
    user_id UUID,
    role TEXT,                    -- 'owner', 'admin', 'member'
    joined_at TIMESTAMP,
    added_by UUID,                -- quem adicionou este membro
    nickname TEXT,                -- apelido dentro do grupo
    notifications_enabled BOOLEAN,
    PRIMARY KEY ((conversation_id), user_id)
);

-- Conversas de cada usuário (índice inverso)
-- Permite listar "todas as conversas do usuário X" ordenadas por última mensagem
CREATE TABLE IF NOT EXISTS user_conversations (
    user_id UUID,
    last_message_at TIMESTAMP,
    conversation_id UUID,
    type TEXT,                    -- 'private' ou 'group'
    name TEXT,                    -- nome do grupo ou display_name do outro usuário
    avatar_url TEXT,              -- foto do grupo ou avatar do outro usuário
    last_message_preview TEXT,    -- prévia da última mensagem
    last_message_sender TEXT,     -- quem enviou a última mensagem
    unread_count INT,
    is_muted BOOLEAN,
    is_pinned BOOLEAN,
    PRIMARY KEY ((user_id), last_message_at, conversation_id)
) WITH CLUSTERING ORDER BY (last_message_at DESC, conversation_id ASC);

-- Lookup rápido: encontrar conversa privada entre dois usuários
-- user_pair é a concatenação ordenada dos dois user_ids: "uuid1:uuid2" onde uuid1 < uuid2
CREATE TABLE IF NOT EXISTS private_conversations (
    organization_id UUID,
    user_pair TEXT,               -- "userId1:userId2" (ordenado alfabeticamente)
    conversation_id UUID,
    created_at TIMESTAMP,
    PRIMARY KEY ((organization_id), user_pair)
);

-- ============================================
-- 3. PRESENÇA (Online/Offline)
-- ============================================

-- Status de presença dos usuários
CREATE TABLE IF NOT EXISTS user_presence (
    user_id UUID PRIMARY KEY,
    status TEXT,                  -- 'online', 'offline', 'away', 'busy'
    last_seen TIMESTAMP,
    connection_id TEXT,           -- ID da conexão WebSocket atual
    device_type TEXT,             -- 'web', 'mobile', 'desktop'
    device_info TEXT,             -- User-Agent ou info do dispositivo
    ip_address TEXT
);

-- Histórico de presença (opcional, para analytics)
CREATE TABLE IF NOT EXISTS presence_history (
    user_id UUID,
    changed_at TIMESTAMP,
    status TEXT,
    connection_id TEXT,
    PRIMARY KEY ((user_id), changed_at)
) WITH CLUSTERING ORDER BY (changed_at DESC)
  AND default_time_to_live = 604800;  -- TTL de 7 dias

-- ============================================
-- 4. MENSAGENS PENDENTES (Offline)
-- ============================================

-- Mensagens que precisam ser entregues quando o usuário ficar online
CREATE TABLE IF NOT EXISTS pending_messages (
    user_id UUID,                 -- destinatário que está offline
    created_at TIMESTAMP,
    message_id UUID,
    conversation_id UUID,
    sender_id UUID,
    sender_name TEXT,             -- display_name do remetente
    content TEXT,
    content_type TEXT,            -- 'text', 'file', 'image', etc.
    file_id TEXT,
    file_name TEXT,
    file_extension TEXT,
    file_size BIGINT,
    file_organization_id TEXT,    -- org do remetente para download
    channel TEXT,                 -- 'whatsapp', 'instagram', etc.
    metadata TEXT,                -- JSON com dados extras
    PRIMARY KEY ((user_id), created_at, message_id)
) WITH CLUSTERING ORDER BY (created_at ASC, message_id ASC)
  AND default_time_to_live = 2592000;  -- TTL de 30 dias

-- Contador de mensagens pendentes por usuário (para badge)
CREATE TABLE IF NOT EXISTS pending_messages_count (
    user_id UUID PRIMARY KEY,
    count COUNTER
);

-- ============================================
-- 5. ÍNDICES SECUNDÁRIOS (para buscas)
-- ============================================

-- Índice para buscar usuários por nome (busca parcial não suportada nativamente)
-- Usaremos LIKE no application layer ou Elasticsearch para busca full-text

-- Índice para buscar conversas por organização
CREATE INDEX IF NOT EXISTS idx_conversations_org ON conversations (organization_id);

-- ============================================
-- 6. DADOS INICIAIS (Seeds)
-- ============================================

-- Inserir alguns usuários de teste
INSERT INTO users (user_id, organization_id, username, display_name, email, created_at, updated_at)
VALUES (d59c17c8-d785-4104-935c-94c3ce01883d, 5a234c5d-fa21-4c30-9a22-d4eaf0beb0be, 'usuario1', 'Usuário Teste 1', 'usuario1@teste.com', toTimestamp(now()), toTimestamp(now()));

INSERT INTO users (user_id, organization_id, username, display_name, email, created_at, updated_at)
VALUES (e1b325ff-f351-4477-a572-cccc3d2ea7f8, ec099857-cd5d-48ec-8273-92fe2dfeb66f, 'usuario2', 'Usuário Teste 2', 'usuario2@teste.com', toTimestamp(now()), toTimestamp(now()));

-- Índices para os usuários de teste
INSERT INTO users_by_username (organization_id, username, user_id, display_name, email)
VALUES (5a234c5d-fa21-4c30-9a22-d4eaf0beb0be, 'usuario1', d59c17c8-d785-4104-935c-94c3ce01883d, 'Usuário Teste 1', 'usuario1@teste.com');

INSERT INTO users_by_username (organization_id, username, user_id, display_name, email)
VALUES (ec099857-cd5d-48ec-8273-92fe2dfeb66f, 'usuario2', e1b325ff-f351-4477-a572-cccc3d2ea7f8, 'Usuário Teste 2', 'usuario2@teste.com');

-- Presença inicial (offline)
INSERT INTO user_presence (user_id, status, last_seen)
VALUES (d59c17c8-d785-4104-935c-94c3ce01883d, 'offline', toTimestamp(now()));

INSERT INTO user_presence (user_id, status, last_seen)
VALUES (e1b325ff-f351-4477-a572-cccc3d2ea7f8, 'offline', toTimestamp(now()));
