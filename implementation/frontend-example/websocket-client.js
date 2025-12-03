// ============================================
// Cliente WebSocket para Status de Mensagens
// ============================================

class ChatStatusClient {
    constructor(apiUrl, token) {
        this.apiUrl = apiUrl.replace('http', 'ws'); // ws:// ou wss://
        this.token = token;
        this.ws = null;
        this.reconnectDelay = 1000;
        this.maxReconnectDelay = 30000;
        this.currentReconnectDelay = this.reconnectDelay;
        this.subscriptions = new Set();
        this.eventHandlers = {
            'message.status': [],
            'connected': [],
            'disconnected': [],
            'subscribed': [],
            'unsubscribed': [],
            'error': []
        };
    }

    /**
     * Conectar ao WebSocket
     */
    connect() {
        const wsUrl = `${this.apiUrl}/ws/status?access_token=${this.token}`;
        console.log('[WebSocket] Conectando...', wsUrl);

        this.ws = new WebSocket(wsUrl);

        this.ws.onopen = () => {
            console.log('[WebSocket] Conectado!');
            this.currentReconnectDelay = this.reconnectDelay;
            
            // Re-subscrever às conversas após reconexão
            this.subscriptions.forEach(conversationId => {
                this._sendSubscribe(conversationId);
            });
        };

        this.ws.onmessage = (event) => {
            try {
                const message = JSON.parse(event.data);
                console.log('[WebSocket] Mensagem recebida:', message);
                this._handleMessage(message);
            } catch (error) {
                console.error('[WebSocket] Erro ao processar mensagem:', error);
            }
        };

        this.ws.onerror = (error) => {
            console.error('[WebSocket] Erro:', error);
            this._triggerEvent('error', error);
        };

        this.ws.onclose = (event) => {
            console.log('[WebSocket] Desconectado:', event.code, event.reason);
            this._triggerEvent('disconnected', { code: event.code, reason: event.reason });
            this._scheduleReconnect();
        };
    }

    /**
     * Inscrever-se para receber atualizações de uma conversa
     */
    subscribe(conversationId) {
        this.subscriptions.add(conversationId);
        this._sendSubscribe(conversationId);
    }

    /**
     * Cancelar inscrição de uma conversa
     */
    unsubscribe(conversationId) {
        this.subscriptions.delete(conversationId);
        this._sendUnsubscribe(conversationId);
    }

    /**
     * Registrar handler para eventos
     */
    on(eventType, handler) {
        if (this.eventHandlers[eventType]) {
            this.eventHandlers[eventType].push(handler);
        }
    }

    /**
     * Desconectar
     */
    disconnect() {
        if (this.ws) {
            this.ws.close(1000, 'Cliente desconectou');
            this.ws = null;
        }
    }

    // ===== Métodos Privados =====

    _sendSubscribe(conversationId) {
        this._sendMessage({
            type: 'subscribe',
            conversationId: conversationId
        });
    }

    _sendUnsubscribe(conversationId) {
        this._sendMessage({
            type: 'unsubscribe',
            conversationId: conversationId
        });
    }

    _sendMessage(message) {
        if (this.ws && this.ws.readyState === WebSocket.OPEN) {
            this.ws.send(JSON.stringify(message));
            console.log('[WebSocket] Mensagem enviada:', message);
        } else {
            console.warn('[WebSocket] Não conectado. Mensagem não enviada:', message);
        }
    }

    _handleMessage(message) {
        const type = message.type;
        
        if (type === 'connected') {
            this._triggerEvent('connected', message);
        } else if (type === 'subscribed') {
            this._triggerEvent('subscribed', message);
        } else if (type === 'unsubscribed') {
            this._triggerEvent('unsubscribed', message);
        } else if (type === 'message.status') {
            this._triggerEvent('message.status', message);
        } else if (type === 'error') {
            this._triggerEvent('error', message);
        } else if (type === 'pong') {
            console.log('[WebSocket] Pong recebido');
        }
    }

    _triggerEvent(eventType, data) {
        const handlers = this.eventHandlers[eventType] || [];
        handlers.forEach(handler => {
            try {
                handler(data);
            } catch (error) {
                console.error(`[WebSocket] Erro no handler de ${eventType}:`, error);
            }
        });
    }

    _scheduleReconnect() {
        console.log(`[WebSocket] Reconectando em ${this.currentReconnectDelay}ms...`);
        
        setTimeout(() => {
            this.connect();
            this.currentReconnectDelay = Math.min(
                this.currentReconnectDelay * 2,
                this.maxReconnectDelay
            );
        }, this.currentReconnectDelay);
    }

    // Ping para manter conexão ativa
    startPing(intervalMs = 30000) {
        setInterval(() => {
            this._sendMessage({ type: 'ping' });
        }, intervalMs);
    }
}

// ============================================
// EXEMPLO DE USO
// ============================================

/*
// 1. Criar instância do cliente
const token = 'seu-jwt-token-aqui';
const client = new ChatStatusClient('http://localhost:5000', token);

// 2. Registrar handlers de eventos
client.on('connected', (data) => {
    console.log('✅ Conectado!', data);
});

client.on('message.status', (data) => {
    console.log('📬 Status atualizado:', data);
    // data = {
    //   type: "message.status",
    //   messageId: "...",
    //   conversationId: "...",
    //   status: "READ",
    //   channel: "whatsapp",
    //   timestamp: "..."
    // }
    
    // Atualizar UI
    updateMessageStatus(data.messageId, data.status);
});

client.on('disconnected', (data) => {
    console.warn('❌ Desconectado:', data);
});

client.on('error', (error) => {
    console.error('⚠️ Erro:', error);
});

// 3. Conectar
client.connect();

// 4. Inscrever em conversas
const conversationId = '123e4567-e89b-12d3-a456-426614174000';
client.subscribe(conversationId);

// 5. Iniciar ping (opcional, para manter conexão ativa)
client.startPing(30000); // 30 segundos

// ===== Funções auxiliares =====

function updateMessageStatus(messageId, status) {
    // Atualizar o elemento na UI
    const messageElement = document.querySelector(`[data-message-id="${messageId}"]`);
    if (messageElement) {
        const statusElement = messageElement.querySelector('.message-status');
        if (statusElement) {
            statusElement.textContent = status;
            statusElement.className = `message-status status-${status.toLowerCase()}`;
        }
    }
}
*/

// Exportar para uso em módulos
if (typeof module !== 'undefined' && module.exports) {
    module.exports = ChatStatusClient;
}
