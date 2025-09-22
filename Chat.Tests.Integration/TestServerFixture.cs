using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Chat.Tests.Integration;

public sealed class TestServerFixture : IAsyncLifetime, IDisposable
{
    private Process? _proc;
    public int Port { get; private set; }

    public async Task InitializeAsync()
    {
        // Escolhe um porto livre:
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();

        // Caminho do projeto Chat.Server (.csproj)
        var root = FindSolutionRoot();
        var serverProj = Path.Combine(root, "Chat.Server", "Chat.Server.csproj");
        if (!File.Exists(serverProj))
            throw new FileNotFoundException("Não encontrei Chat.Server.csproj em " + serverProj);

        // Build rápido (garante que está compilado)
        await RunProcess("dotnet", $"build \"{serverProj}\" -c Debug");

        // Sobe o servidor com "dotnet run -- --port"
        var psi = new ProcessStartInfo("dotnet", $"run --project \"{serverProj}\" -- {Port}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["DOTNET_NOLOGO"] = "1";

        _proc = Process.Start(psi)!;

        // Espera até log "Escutando" ou 10s
        var started = await WaitForOutput(_proc, "Escutando", TimeSpan.FromSeconds(10));
        if (!started)
        {
            var err = await _proc.StandardError.ReadToEndAsync();
            throw new Exception($"Servidor não iniciou. STDERR:\n{err}");
        }
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false })
            {
                _proc.Kill(true);
                _proc.WaitForExit(3000);
            }
        }
        catch { /* ignore */ }
    }

    private static async Task RunProcess(string file, string args)
    {
        var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new Exception($"{file} {args} falhou com código {p.ExitCode}");
    }

    private static async Task<bool> WaitForOutput(Process proc, string token, TimeSpan timeout)
    {
        var src = new CancellationTokenSource(timeout);
        try
        {
            while (!src.IsCancellationRequested && !proc.HasExited)
            {
                var line = await proc.StandardOutput.ReadLineAsync();
                if (line?.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch { }
        return false;
    }

    private static string FindSolutionRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        for (int i = 0; i < 10; i++)
        {
            if (Directory.GetFiles(dir, "*.sln").Any() && Directory.Exists(Path.Combine(dir, "Chat.Server")))
                return dir;
            dir = Directory.GetParent(dir)!.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}

[CollectionDefinition("server")]
public sealed class ServerCollection : ICollectionFixture<TestServerFixture> { }
