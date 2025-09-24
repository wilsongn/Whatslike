| Flag               | Exemplo                            |          Default | Para que serve                                                                     |
| ------------------ | ---------------------------------- | ---------------: | ---------------------------------------------------------------------------------- |
| `PORT`             | `5000`                             |           `5000` | Porta TCP do servidor.                                                             |
| `NODE_ID`          | `A`, `B`, `hostname`               |    `MachineName` | Identifica o nó (usado em presença e roteamento).                                  |
| `REDIS_URL`        | `localhost:6379`                   | `localhost:6379` | Endereço do Redis (host\:porta). Use `disabled` para rodar sem Redis (fallback).   |
| `REDIS_SSL`        | `true`/`false`                     |          `false` | Ativa TLS para conectar ao Redis (ElastiCache com in-transit encryption, por ex.). |
| `REDIS_SSL_HOST`   | `mycache.xxxx.cache.amazonaws.com` |                — | SNI/validação de host TLS do Redis (se `REDIS_SSL=true`).                          |
| `HEARTBEAT_SEC`    | `30`                               |             `30` | Intervalo do Ping/Pong (gestão de presença).                                       |
| `IDLE_TIMEOUT_SEC` | `90`                               |             `90` | Fecha conexões inativas acima desse tempo.                                         |
| `PING_LOG`         | `true`/`false`                     |          `false` | Loga `[Ping]/[Pong]` no console.                                                   |
| `DEMO_VERBOSE`     | `true`/`false`                     |           `true` | Mostra logs de roteamento + snapshot `[Stats]`.                                    |


```bash
# 1) Certifique-se de que o Docker Desktop está "Docker is running"
docker pull redis:7
docker run --name redis -p 6379:6379 -d redis:7

# Verificar containers em execução
docker ps

# Verificar logs do Redis
docker logs redis --tail 20

net localgroup docker-users $env:USERNAME /add
docker run -p 6379:6379 redis:7
```

```powershell
# Variáveis de ambiente
$env:REDIS_URL = "localhost:6379"
$env:NODE_ID = "A"
$env:PING_LOG = "true"
$env:DEMO_VERBOSE = "true"

# Usando porta da env:
$env:PORT = "5000"
dotnet run --project Chat.Server

# OU passando a porta como argumento (sobrescreve PORT):
dotnet run --project Chat.Server -- 5000
```



