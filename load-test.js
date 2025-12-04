// tests/load-test.js
// Script k6 para teste de carga do Chat4All
//
// Executar: k6 run --vus 10 --duration 30s load-test.js
// Ou: k6 run --vus 50 --duration 2m load-test.js

import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate, Trend } from 'k6/metrics';

// Métricas customizadas
const errorRate = new Rate('errors');
const messageSendTime = new Trend('message_send_duration');

// Configuração
export const options = {
    stages: [
        { duration: '30s', target: 10 },  // Ramp-up para 10 usuários
        { duration: '1m', target: 50 },   // Ramp-up para 50 usuários
        { duration: '2m', target: 50 },   // Manter 50 usuários
        { duration: '30s', target: 100 }, // Pico de 100 usuários
        { duration: '1m', target: 0 },    // Ramp-down
    ],
    thresholds: {
        http_req_duration: ['p(95)<500'], // 95% das requisições < 500ms
        errors: ['rate<0.1'],              // Taxa de erro < 10%
    },
};

// Token JWT (gerar com seu script)
const JWT_TOKEN = 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...'; // SUBSTITUIR

const BASE_URL = 'http://localhost:8080';

export function setup() {
    console.log('Iniciando teste de carga...');
    return { startTime: new Date() };
}

export default function () {
    const conversationId = `load-test-${__VU}-${__ITER}`;
    const messageId = `msg-${Date.now()}-${__VU}-${__ITER}`;

    const payload = JSON.stringify({
        messageId: messageId,
        conversationId: conversationId,
        content: `Load test message from VU ${__VU} iteration ${__ITER}`,
    });

    const params = {
        headers: {
            'Authorization': `Bearer ${JWT_TOKEN}`,
            'Content-Type': 'application/json',
        },
        timeout: '60s',
    };

    // Enviar mensagem
    const startTime = Date.now();
    const res = http.post(`${BASE_URL}/api/v1/messages`, payload, params);
    const duration = Date.now() - startTime;

    messageSendTime.add(duration);

    // Validar resposta
    const checkResult = check(res, {
        'status is 200': (r) => r.status === 200,
        'has messageId': (r) => JSON.parse(r.body).messageId !== undefined,
        'status is accepted': (r) => JSON.parse(r.body).status === 'accepted',
    });

    errorRate.add(!checkResult);

    if (res.status !== 200) {
        console.error(`Error sending message: ${res.status} ${res.body}`);
    }

    // Aguardar entre 1-3 segundos antes da próxima iteração
    sleep(Math.random() * 2 + 1);
}

export function teardown(data) {
    const duration = (new Date() - data.startTime) / 1000;
    console.log(`Teste concluído! Duração: ${duration.toFixed(2)}s`);
}

export function handleSummary(data) {
    return {
        'summary.json': JSON.stringify(data),
        stdout: textSummary(data, { indent: ' ', enableColors: true }),
    };
}

function textSummary(data, options) {
    const indent = options.indent || '';
    const enableColors = options.enableColors || false;

    let summary = '\n';
    summary += `${indent}Teste de Carga - Resultados\n`;
    summary += `${indent}${'='.repeat(50)}\n\n`;

    // Requisições
    summary += `${indent}Total de requisições: ${data.metrics.http_reqs.values.count}\n`;
    summary += `${indent}Requisições/seg: ${data.metrics.http_reqs.values.rate.toFixed(2)}\n`;
    summary += `${indent}Taxa de erro: ${(data.metrics.errors.values.rate * 100).toFixed(2)}%\n\n`;

    // Latência
    summary += `${indent}Latência (ms):\n`;
    summary += `${indent}  - Média: ${data.metrics.http_req_duration.values.avg.toFixed(2)}\n`;
    summary += `${indent}  - Min: ${data.metrics.http_req_duration.values.min.toFixed(2)}\n`;
    summary += `${indent}  - Max: ${data.metrics.http_req_duration.values.max.toFixed(2)}\n`;
    summary += `${indent}  - p(50): ${data.metrics.http_req_duration.values['p(50)'].toFixed(2)}\n`;
    summary += `${indent}  - p(90): ${data.metrics.http_req_duration.values['p(90)'].toFixed(2)}\n`;
    summary += `${indent}  - p(95): ${data.metrics.http_req_duration.values['p(95)'].toFixed(2)}\n`;
    summary += `${indent}  - p(99): ${data.metrics.http_req_duration.values['p(99)'].toFixed(2)}\n\n`;

    // VUs
    summary += `${indent}Virtual Users:\n`;
    summary += `${indent}  - Máximo: ${data.metrics.vus.values.max}\n`;
    summary += `${indent}  - Mínimo: ${data.metrics.vus.values.min}\n\n`;

    return summary;
}