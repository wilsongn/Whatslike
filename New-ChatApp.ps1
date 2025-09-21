Param(
    [string]$SolutionName = "ChatApp",
    [string]$Framework = "net8.0",
    [int]$Port = 5000
)

$Server = "Chat.Server"
$Client = "Chat.Client.Cli"
$Shared = "Chat.Shared"
$Tests  = "Chat.Tests"

function Ensure-DotNet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Error "Erro: 'dotnet' não encontrado no PATH. Instale o .NET SDK 8+."
        exit 1
    }
}

function New-Dir([string]$Path) {
    if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Force -Path $Path | Out-Null }
}

function Write-Utf8([string]$Path, [string]$Content) {
    New-Dir (Split-Path $Path -Parent)
    # Use BOM-less UTF8
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

Ensure-DotNet

Write-Host "==> Criando solução e projetos (.NET CLI)"
dotnet new sln -n $SolutionName | Out-Null
Push-Location $SolutionName

dotnet new classlib -n $Shared -f $Framework | Out-Null
dotnet new console -n $Server -f $Framework | Out-Null
dotnet new console -n $Client -f $Framework | Out-Null
dotnet new xunit   -n $Tests  -f $Framework | Out-Null

dotnet sln add "$Shared/$Shared.csproj" "$Server/$Server.csproj" "$Client/$Client.csproj" "$Tests/$Tests.csproj" | Out-Null
dotnet add "$Server/$Server.csproj" reference "$Shared/$Shared.csproj" | Out-Null
dotnet add "$Client/$Client.csproj" reference "$Shared/$Shared.csproj" | Out-Null
dotnet add "$Tests/$Tests.csproj"   reference "$Shared/$Shared.csproj" | Out-Null

Write-Host "==> Estrutura de pastas"
New-Dir "$Shared/Protocol"
New-Dir "$Shared/Net"
New-Dir "$Server/Hub"
New-Dir "$Client/Commands"

Write-Host "==> Escrevendo código base"

# ----------------- Chat.Shared -----------------
Write-Utf8 "$Shared/Protocol/MessageType.cs" @"
using System;

namespace Chat.Shared.Protocol
{
    public enum MessageType
    {
        Auth = 1,
        PrivateMsg = 2,
        GroupMsg = 3,
        FileChunk = 4,
        Ack = 5,
        Error = 6,
        ListUsers = 7,
        ListGroups = 8,
        CreateGroup = 9,
        AddToGroup = 10,
        Ping = 11,
        Pong = 12
    }
}
"@

Write-Utf8 "$Shared/Protocol/Envelope.cs" @"
using System;

namespace Chat.Shared.Protocol
{
    /// <summary>
    /// Envelopa qualquer mensagem. O Payload é JSON (camelCase).
    /// </summary>
    public record Envelope(MessageType Type, string? From, string? To, string Payload);
}
"@

Write-Utf8 "$Shared/Protocol/Messages.cs" @"
using System;

namespace Chat.Shared.Protocol
{
    // Autenticação
    public record AuthRequest(string Username, string? Password);

    // Texto direto
    public record PrivateMessage(string To, string Text);

    // Grupo
    public record GroupMessage(string Group, string Text);
    public record CreateGroupRequest(string Name);
    public record AddToGroupRequest(string Name, string Username);

    // Arquivos (WIP - scaffold)
    public record FileChunkHeader(string Id, string Target, string FileName, long TotalBytes, int ChunkSize);
    public record FileChunk(string Id, int Index, int Count, byte[] Data);

    // Administração/Consultas
    public record ListUsersRequest();
    public record ListUsersResponse(string[] Users);

    // Controle/Infra
    public record AckMessage(string CorrelationId, string? Note);
    public record ErrorMessage(string Code, string Message);
}
"@

Write-Utf8 "$Shared/Net/JsonMessageSerializer.cs" @"
using System.Text.Json;

namespace Chat.Shared.Net
{
    public static class JsonMessageSerializer
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        public static string Serialize<T>(T obj) =>
            JsonSerializer.Serialize(obj, Options);

        public static T? Deserialize<T>(string json) =>
            JsonSerializer.Deserialize<T>(json, Options);
    }

    public static class ProtocolUtil
    {
        public static Chat.Shared.Protocol.Envelope Make<T>(
            Chat.Shared.Protocol.MessageType type,
            string? from, string? to, T payload) =>
            new(type, from, to, JsonMessageSerializer.Serialize(payload));
    }
}
"@

Write-Utf8 "$Shared/Net/Framing.cs" @"
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Chat.Shared.Net
{
    /// <summary>
    /// Framing simples: 4 bytes (int32 LE) de tamanho + payload binário.
    /// </summary>
    public static class Framing
    {
        public static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken ct = default)
        {
            var lenBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lenBytes, payload.Length);
            await stream.WriteAsync(lenBytes, ct).ConfigureAwait(false);
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public static async Task<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
        {
            var lenBytes = await ReadExactAsync(stream, 4, ct).ConfigureAwait(false);
            if (lenBytes is null) return null;
            int len = BinaryPrimitives.ReadInt32LittleEndian(lenBytes);
            if (len < 0) return null;
            return await ReadExactAsync(stream, len, ct).ConfigureAwait(false);
        }

        private static async Task<byte[]?> ReadExactAsync(Stream stream, int len, CancellationToken ct)
        {
            var buffer = new byte[len];
            int read = 0;
            while (read < len)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, len - read), ct).ConfigureAwait(false);
                if (n == 0) return null; // desconexão
                read += n;
            }
            return buffer;
        }
    }
}
"@

# ----------------- Chat.Server -----------------
Write-Utf8 "$Server/Program.cs" @"
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;
using Chat.Server.Hub;

var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 5000;
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
Console.WriteLine($""[Server] Escutando em 0.0.0.0:{port}"");

var sessions = new SessionManager();
var groups = new GroupManager();
var router  = new Router(sessions, groups);

_ = Task.Run(async () =>
{
    while (true)
    {
        var client = await listener.AcceptTcpClientAsync();
        _ = Task.Run(() => HandleClientAsync(client));
    }
});

Console.WriteLine(""[Server] Pressione Ctrl+C para encerrar."");
await Task.Delay(-1);

async Task HandleClientAsync(TcpClient tcp)
{
    var stream = tcp.GetStream();
    ClientSession? session = null;

    try
    {
        // 1) Espera AUTH
        var first = await Framing.ReadFrameAsync(stream);
        if (first is null) { tcp.Close(); return; }

        var env = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(first));
        if (env is null || env.Type != MessageType.Auth)
        {
            await SendErrorAsync(stream, ""AUTH_REQUIRED"", ""Primeira mensagem deve ser AUTH"");
            tcp.Close(); return;
        }

        var auth = JsonMessageSerializer.Deserialize<AuthRequest>(env.Payload);
        if (auth is null || string.IsNullOrWhiteSpace(auth.Username))
        {
            await SendErrorAsync(stream, ""INVALID_AUTH"", ""Username inválido"");
            tcp.Close(); return;
        }

        if (!sessions.TryAdd(auth.Username, tcp))
        {
            await SendErrorAsync(stream, ""USERNAME_TAKEN"", ""Usuário já conectado"");
            tcp.Close(); return;
        }

        session = sessions.Get(auth.Username)!;
        Console.WriteLine($""[Server] {auth.Username} conectado."");

        // ACK de login
        await session.SendAsync(ProtocolUtil.Make(MessageType.Ack, ""server"", auth.Username, new AckMessage(""login"", ""ok"")));

        // 2) Loop de mensagens
        while (true)
        {
            var frame = await Framing.ReadFrameAsync(stream);
            if (frame is null) break;
            var envelope = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(frame));
            if (envelope is null) continue;

            await router.HandleAsync(session, envelope);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($""[Server] erro: {ex.Message}"");
    }
    finally
    {
        if (session is not null)
        {
            sessions.Remove(session.Username);
            Console.WriteLine($""[Server] {session.Username} saiu."");
        }
        tcp.Close();
    }
}

static async Task SendErrorAsync(NetworkStream stream, string code, string message)
{
    var env = ProtocolUtil.Make(MessageType.Error, ""server"", null, new ErrorMessage(code, message));
    var json = JsonSerializer.Serialize(env);
    await Framing.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json));
}
"@

Write-Utf8 "$Server/Hub/ClientSession.cs" @"
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Server.Hub
{
    public class ClientSession
    {
        public string Username { get; }
        public TcpClient Client { get; }
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _sendLock = new(1,1);

        public ClientSession(string username, TcpClient client)
        {
            Username = username;
            Client = client;
            _stream = client.GetStream();
        }

        public async Task SendAsync(Envelope env, CancellationToken ct = default)
        {
            var json = JsonSerializer.Serialize(env);
            var bytes = Encoding.UTF8.GetBytes(json);
            await _sendLock.WaitAsync(ct);
            try
            {
                await Framing.WriteFrameAsync(_stream, bytes, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }
    }
}
"@

Write-Utf8 "$Server/Hub/SessionManager.cs" @"
using System.Collections.Concurrent;
using System.Net.Sockets;

namespace Chat.Server.Hub
{
    public class SessionManager
    {
        private readonly ConcurrentDictionary<string, ClientSession> _byUser = new();

        public bool TryAdd(string username, TcpClient client) =>
            _byUser.TryAdd(username, new ClientSession(username, client));

        public void Remove(string username) => _byUser.TryRemove(username, out _);

        public ClientSession? Get(string username) => _byUser.TryGetValue(username, out var s) ? s : null;

        public IEnumerable<string> ListUsers() => _byUser.Keys;
    }
}
"@

Write-Utf8 "$Server/Hub/GroupManager.cs" @"
using System.Collections.Concurrent;

namespace Chat.Server.Hub
{
    public class GroupManager
    {
        private readonly ConcurrentDictionary<string, HashSet<string>> _groups = new();

        public bool Create(string name) => _groups.TryAdd(name, new HashSet<string>());

        public bool AddUser(string name, string username)
        {
            var set = _groups.GetValueOrDefault(name);
            if (set is null) return false;
            lock(set) return set.Add(username);
        }

        public IEnumerable<string> GetMembers(string name) =>
            _groups.GetValueOrDefault(name) ?? Enumerable.Empty<string>();

        public IEnumerable<string> ListGroups() => _groups.Keys;
    }
}
"@

Write-Utf8 "$Server/Hub/Router.cs" @"
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Server.Hub
{
    public class Router
    {
        private readonly SessionManager _sessions;
        private readonly GroupManager _groups;

        public Router(SessionManager sessions, GroupManager groups)
        {
            _sessions = sessions;
            _groups = groups;
        }

        public async Task HandleAsync(ClientSession sender, Envelope env)
        {
            switch (env.Type)
            {
                case MessageType.PrivateMsg:
                    await HandlePrivateAsync(sender, env);
                    break;
                case MessageType.GroupMsg:
                    await HandleGroupAsync(sender, env);
                    break;
                case MessageType.CreateGroup:
                    await HandleCreateGroupAsync(sender, env);
                    break;
                case MessageType.AddToGroup:
                    await HandleAddToGroupAsync(sender, env);
                    break;
                case MessageType.ListUsers:
                    await HandleListUsersAsync(sender);
                    break;
                default:
                    await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, ""server"", sender.Username, new ErrorMessage(""UNSUPPORTED"", $""Tipo {env.Type}"")));
                    break;
            }
        }

        private async Task HandlePrivateAsync(ClientSession sender, Envelope env)
        {
            var pm = JsonMessageSerializer.Deserialize<PrivateMessage>(env.Payload);
            if (pm is null || string.IsNullOrWhiteSpace(pm.To))
            {
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, ""server"", sender.Username, new ErrorMessage(""BAD_REQUEST"", ""Destino inválido"")));
                return;
            }
            var target = _sessions.Get(pm.To);
            if (target is null)
            {
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, ""server"", sender.Username, new ErrorMessage(""NOT_FOUND"", ""Usuário não conectado"")));
                return;
            }

            var deliver = new Envelope(MessageType.PrivateMsg, sender.Username, pm.To, env.Payload);
            await target.SendAsync(deliver);
            await sender.SendAsync(ProtocolUtil.Make(MessageType.Ack, ""server"", sender.Username, new AckMessage(""pm"", ""entregue"")));
        }

        private async Task HandleGroupAsync(ClientSession sender, Envelope env)
        {
            var gm = JsonMessageSerializer.Deserialize<GroupMessage>(env.Payload);
            if (gm is null || string.IsNullOrWhiteSpace(gm.Group))
            {
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, ""server"", sender.Username, new ErrorMessage(""BAD_REQUEST"", ""Grupo inválido"")));
                return;
            }

            foreach (var member in _groups.GetMembers(gm.Group))
            {
                if (member == sender.Username) continue;
                var target = _sessions.Get(member);
                if (target is not null)
                {
                    var deliver = new Envelope(MessageType.GroupMsg, sender.Username, gm.Group, env.Payload);
                    await target.SendAsync(deliver);
                }
            }
            await sender.SendAsync(ProtocolUtil.Make(MessageType.Ack, ""server"", sender.Username, new AckMessage(""gmsg"", ""distribuído"")));
        }

        private async Task HandleCreateGroupAsync(ClientSession sender, Envelope env)
        {
            var req = JsonMessageSerializer.Deserialize<CreateGroupRequest>(env.Payload);
            if (req is null || string.IsNullOrWhiteSpace(req.Name))
            {
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, ""server"", sender.Username, new ErrorMessage(""BAD_REQUEST"", ""Nome inválido"")));
                return;
            }
            if (_groups.Create(req.Name))
            {
                // criador entra automaticamente
                _groups.AddUser(req.Name, sender.Username);
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Ack, ""server"", sender.Username, new AckMessage(""createGroup"", ""ok"")));
            }
            else
            {
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, ""server"", sender.Username, new ErrorMessage(""CONFLICT"", ""Grupo já existe"")));
            }
        }

        private async Task HandleAddToGroupAsync(ClientSession sender, Envelope env)
        {
            var req = JsonMessageSerializer.Deserialize<AddToGroupRequest>(env.Payload);
            if (req is null || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Username))
            {
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, ""server"", sender.Username, new ErrorMessage(""BAD_REQUEST"", ""Dados inválidos"")));
                return;
            }
            if (_groups.AddUser(req.Name, req.Username))
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Ack, ""server"", sender.Username, new AckMessage(""addToGroup"", ""ok"")));
            else
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, ""server"", sender.Username, new ErrorMessage(""NOT_FOUND"", ""Grupo não existe ou usuário já é membro"")));
        }

        private async Task HandleListUsersAsync(ClientSession sender)
        {
            var list = _sessions.ListUsers().ToArray();
            await sender.SendAsync(ProtocolUtil.Make(MessageType.Ack, ""server"", sender.Username, new ListUsersResponse(list)));
        }
    }
}
"@

# ----------------- Chat.Client.Cli -----------------
Write-Utf8 "$Client/Program.cs" @"
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

var host = ""127.0.0.1"";
var port = 5000;
string? username = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case ""--host"": host = args[++i]; break;
        case ""--port"": port = int.Parse(args[++i]); break;
        case ""--user"": username = args[++i]; break;
    }
}

Console.WriteLine($""[Client] Conectando em {host}:{port} ..."");
var tcp = new TcpClient();
await tcp.ConnectAsync(host, port);
var stream = tcp.GetStream();

while (string.IsNullOrWhiteSpace(username))
{
    Console.Write(""Username: "");
    username = Console.ReadLine();
}

await SendAsync(ProtocolUtil.Make(MessageType.Auth, null, null, new AuthRequest(username!, null)));

Console.WriteLine(""Comandos: /msg <user> <texto> | /users | /group create <nome> | /group add <nome> <user> | /gmsg <grupo> <texto> | /quit"");

var cts = new CancellationTokenSource();
_ = Task.Run(() => ReceiveLoopAsync(cts.Token));

while (true)
{
    var line = Console.ReadLine();
    if (line is null) continue;
    if (line.Equals(""/quit"", StringComparison.OrdinalIgnoreCase)) break;

    if (line.StartsWith(""/msg ""))
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) { Console.WriteLine(""uso: /msg <user> <texto>""); continue; }
        var payload = new PrivateMessage(parts[1], parts[2]);
        await SendAsync(ProtocolUtil.Make(MessageType.PrivateMsg, username, parts[1], payload));
        continue;
    }
    if (line.Equals(""/users"", StringComparison.OrdinalIgnoreCase))
    {
        await SendAsync(ProtocolUtil.Make(MessageType.ListUsers, username, null, new ListUsersRequest()));
        continue;
    }
    if (line.StartsWith(""/group create ""))
    {
        var name = line.Substring(""/group create "".Length).Trim();
        await SendAsync(ProtocolUtil.Make(MessageType.CreateGroup, username, null, new CreateGroupRequest(name)));
        continue;
    }
    if (line.StartsWith(""/group add ""))
    {
        var parts = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) { Console.WriteLine(""uso: /group add <grupo> <user>""); continue; }
        await SendAsync(ProtocolUtil.Make(MessageType.AddToGroup, username, null, new AddToGroupRequest(parts[2], parts[3])));
        continue;
    }
    if (line.StartsWith(""/gmsg ""))
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) { Console.WriteLine(""uso: /gmsg <grupo> <texto>""); continue; }
        await SendAsync(ProtocolUtil.Make(MessageType.GroupMsg, username, parts[1], new GroupMessage(parts[1], parts[2])));
        continue;
    }

    Console.WriteLine(""comando não reconhecido."");
}

cts.Cancel();
tcp.Close();

async Task SendAsync(Envelope env)
{
    var json = JsonSerializer.Serialize(env);
    await Chat.Shared.Net.Framing.WriteFrameAsync(stream, Encoding.UTF8.GetBytes(json));
}

async Task ReceiveLoopAsync(CancellationToken ct)
{
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await Framing.ReadFrameAsync(stream, ct);
            if (frame is null) break;
            var env = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(frame));
            if (env is null) continue;

            switch (env.Type)
            {
                case MessageType.PrivateMsg:
                    var pm = JsonMessageSerializer.Deserialize<PrivateMessage>(env.Payload);
                    Console.WriteLine($""[PM] {env.From} → você: {pm?.Text}"");
                    break;
                case MessageType.GroupMsg:
                    var gm = JsonMessageSerializer.Deserialize<GroupMessage>(env.Payload);
                    Console.WriteLine($""[G:{gm?.Group}] {env.From}: {gm?.Text}"");
                    break;
                case MessageType.Ack:
                    // Pode ser AckMessage ou ListUsersResponse
                    try
                    {
                        var list = JsonMessageSerializer.Deserialize<ListUsersResponse>(env.Payload);
                        if (list?.Users is not null)
                        {
                            Console.WriteLine(""[Users] "" + string.Join("", "", list.Users));
                            break;
                        }
                    }
                    catch { /* ignore */ }
                    Console.WriteLine(""[Ack] "" + env.Payload);
                    break;
                case MessageType.Error:
                    var err = JsonMessageSerializer.Deserialize<ErrorMessage>(env.Payload);
                    Console.WriteLine($""[Erro] {err?.Code}: {err?.Message}"");
                    break;
                default:
                    Console.WriteLine($""[Info] {env.Type}: {env.Payload}"");
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine(""[Client] desconectado: "" + ex.Message);
    }
}
"@

# ----------------- Chat.Tests -----------------
Write-Utf8 "$Tests/FramingTests.cs" @"
using System.Text;
using Chat.Shared.Net;
using Xunit;

public class FramingTests
{
    [Fact]
    public async Task Roundtrip_Works()
    {
        using var ms = new MemoryStream();
        var payload = Encoding.UTF8.GetBytes(""hello"");
        await Framing.WriteFrameAsync(ms, payload);
        ms.Position = 0;
        var read = await Framing.ReadFrameAsync(ms);
        Assert.NotNull(read);
        Assert.Equal(payload, read);
    }
}
"@

Write-Utf8 "$Tests/RouterTests.cs" @"
using Xunit;

public class RouterTests
{
    [Fact(Skip=""Exemplo - implementar com fakes/mocks dos managers"")]
    public void Todo()
    {
        Assert.True(true);
    }
}
"@

Write-Host "==> Build"
dotnet build -v q

Write-Host ""
Write-Host "Pronto! Como rodar:"
Write-Host "  dotnet run --project $Server -- $Port"
Write-Host "  dotnet run --project $Client -- --host 127.0.0.1 --port $Port --user alice"
Write-Host "  dotnet run --project $Client -- --user bob"
Pop-Location
