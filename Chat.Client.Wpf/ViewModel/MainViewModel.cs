using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Chat.Client.Wpf.Infrastructure;
using Chat.Client.Wpf.Models;
using Chat.Client.Wpf.Services;
using Microsoft.Win32;
using Chat.Grpc;

// --- USINGS NECESSÁRIOS PARA AS CORREÇÕES ---
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace Chat.Client.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ISocketChatClient _chat;
    private readonly IDialogService _dialog;

    private string? _jwtToken;

    // --- gRPC publisher ---
    private GrpcPublisher? _grpc;
    private int _grpcPort = 6000;
    public int GrpcPort { get => _grpcPort; set => SetProperty(ref _grpcPort, value); }

    private static void UI(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    public MainViewModel(ISocketChatClient chat, IDialogService dialog)
    {
        _chat = chat;
        _dialog = dialog;

        _chat.OnPrivate += (from, text) => UI(() => HandlePrivate(from, text));
        _chat.OnGroup += (group, from, text) => UI(() => HandleGroup(group, from, text));
        _chat.OnUsers += users => UI(() => Info = "[Users] " + string.Join(", ", users));
        _chat.OnFileSaved += e => UI(() => HandleFileSaved(e));
        _chat.OnSendProgress += (s, t) => UI(() => ProgressText = t > 0 ? $"{(double)s / t:0.0%}" : "");
        _chat.OnInfo += msg => UI(() => Info = msg);
        _chat.OnError += msg => UI(() => Info = msg);

        ConnectCommand = new RelayCommand(async _ =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Username)) 
                {
                    Info = "Digite um usuário.";
                    return;
                }
                
                // Gera o token automaticamente
                _jwtToken = GenerateDevToken(Username); 

                await _chat.ConnectAsync(Host, Port, Username);

                if (_grpc is not null) { try { await _grpc.DisposeAsync(); } catch { } }
                _grpc = new GrpcPublisher($"https://localhost:{GrpcPort}", () => _jwtToken);

                Info = $"Conectado como {Username} em {Host}:{Port} (gRPC ativo)";
            }
            catch (Exception ex) { Info = "[Erro] " + ex.Message; }
        });

        SendCommand = new RelayCommand(async _ => await SendAsync(), _ => Selected is not null && !string.IsNullOrWhiteSpace(Compose));
        AttachCommand = new RelayCommand(async _ => await AttachAsync(), _ => Selected is not null);
        ListUsersCommand = new RelayCommand(async _ => await _chat.ListUsersAsync());
        
        NewDirectCommand = new RelayCommand(_ =>
        {
            var user = _dialog.Prompt("Nova conversa privada", "Username do usuário:");
            if (string.IsNullOrWhiteSpace(user)) return;
            var conv = EnsureConversation(ConversationType.Direct, user);
            Selected = conv;
            Info = $"Conversa com {user} criada.";
        });

        NewGroupCommand = new RelayCommand(async _ =>
        {
            var name = _dialog.Prompt("Novo grupo", "Nome do grupo:");
            if (string.IsNullOrWhiteSpace(name)) return;
            await _chat.CreateGroupAsync(name);
            var conv = EnsureConversation(ConversationType.Group, name);
            Selected = conv;
            Info = $"Grupo {name} criado.";
        });

        AddMemberToGroupCommand = new RelayCommand(async param =>
        {
            var conv = param as Conversation ?? Selected;
            if (conv is null || conv.Type != ConversationType.Group)
            {
                Info = "Selecione um grupo para adicionar membros.";
                return;
            }
            var member = _dialog.Prompt("Adicionar membro", $"Usuário para adicionar em '{conv.Id}':");
            if (string.IsNullOrWhiteSpace(member)) return;

            await _chat.AddToGroupAsync(conv.Id, member);
            Info = $"Usuário '{member}' adicionado ao grupo '{conv.Id}'.";
        });
    }

    // --- Bindables ---
    private string _host = "127.0.0.1";
    public string Host { get => _host; set => SetProperty(ref _host, value); }

    private int _port = 5000;
    public int Port { get => _port; set => SetProperty(ref _port, value); }

    private string _username = "";
    public string Username { get => _username; set => SetProperty(ref _username, value); }

    private string _info = "";
    public string Info { get => _info; set => SetProperty(ref _info, value); }

    private string _progress = "0%";
    public string ProgressText { get => _progress; set => SetProperty(ref _progress, value); }

    public ObservableCollection<Conversation> Directs { get; } = new();
    public ObservableCollection<Conversation> Groups { get; } = new();

    private Conversation? _selected;
    public Conversation? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
            {
                value.Unread = 0;
                // Chama as novas funções ao selecionar
                _ = SyncHistoryAsync(value); 
                _ = MarkAsReadAsync(value.Id);
            }

            (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AttachCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private Conversation? _selectedDirect;
    public Conversation? SelectedDirect
    {
        get => _selectedDirect;
        set
        {
            if (SetProperty(ref _selectedDirect, value))
            {
                if (value is not null)
                {
                    if (_selectedGroup is not null) { _selectedGroup = null; RaisePropertyChanged(nameof(SelectedGroup)); }
                    Selected = value;
                }
                (AttachCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private Conversation? _selectedGroup;
    public Conversation? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (SetProperty(ref _selectedGroup, value))
            {
                if (value is not null)
                {
                    if (_selectedDirect is not null) { _selectedDirect = null; RaisePropertyChanged(nameof(SelectedDirect)); }
                    Selected = value;
                }
                (AttachCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }
    }

    private string _compose = "";
    public string Compose
    {
        get => _compose;
        set
        {
            if (SetProperty(ref _compose, value))
                (SendCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    // --- Commands ---
    public ICommand ConnectCommand { get; }
    public ICommand ListUsersCommand { get; }
    // Removidos os comandos duplicados/não usados que causavam erro
    public ICommand NewDirectCommand { get; }
    public ICommand NewGroupCommand { get; }
    public ICommand AddMemberToGroupCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand AttachCommand { get; }

    // --- Métodos Privados ---

    private void HandlePrivate(string from, string text)
    {
        var c = EnsureConversation(ConversationType.Direct, from);
        var isActive = Selected == c;
        c.Add(new MessageItem { From = from, Text = text, IsMine = false }, isActive);
    }

    private void HandleGroup(string group, string from, string text)
    {
        var c = EnsureConversation(ConversationType.Group, group);
        var isActive = Selected == c;
        c.Add(new MessageItem { From = from, Text = text, IsMine = false }, isActive);
    }

    private void HandleFileSaved(FileSavedArgs e)
    {
        var type = Groups.Any(g => g.Id == e.Target) ? ConversationType.Group : ConversationType.Direct;
        var c = EnsureConversation(type, type == ConversationType.Group ? e.Target : e.From);
        var isActive = Selected == c;
        var saveDir = Path.GetDirectoryName(e.Path) ?? e.Path;
        c.Add(new MessageItem
        {
            From = e.From,
            Text = $"[ARQUIVO] Recebido: {e.FileName}\nSalvo em: {saveDir}",
            IsMine = false,
            IsFile = true,
            FilePath = e.Path,
            FileName = e.FileName,
            FileSize = e.TotalBytes,
        }, isActive);
    }

    private Conversation EnsureConversation(ConversationType type, string id)
    {
        var list = type == ConversationType.Direct ? Directs : Groups;
        var conv = list.FirstOrDefault(x => x.Id == id);
        if (conv is not null) return conv;

        conv = new Conversation { Type = type, Id = id, DisplayName = id };
        list.Add(conv);
        if (Selected is null) Selected = conv;
        return conv;
    }

    private async Task SendAsync()
    {
        if (Selected is null || string.IsNullOrWhiteSpace(Compose)) return;
        var text = Compose;
        Compose = string.Empty;

        bool sent = false;

        try
        {
            if (_grpc is not null)
            {
                if (Selected.Type == ConversationType.Direct)
                    sent = await _grpc.PublishPrivateAsync(Username, Selected.Id, text);
                else
                    sent = await _grpc.PublishGroupAsync(Username, Selected.Id, text);
            }

            if (!sent)
            {
                if (Selected.Type == ConversationType.Direct)
                    await _chat.SendPrivateTextAsync(Selected.Id, text);
                else
                    await _chat.SendGroupTextAsync(Selected.Id, text);
                sent = true;
            }
        }
        catch (Exception ex)
        {
            Info = "[Envio] falhou: " + ex.Message;
        }

        if (sent)
        {
            Selected.Add(new MessageItem { From = Username, Text = text, IsMine = true }, isActive: true);
        }
    }

    private async Task AttachAsync()
    {
        try
        {
            if (Selected is null) return;

            var dlg = new OpenFileDialog { Title = "Selecione o arquivo", CheckFileExists = true };
            if (dlg.ShowDialog() == true)
            {
                await _chat.SendFileAsync(Selected.Id, dlg.FileName);
                Selected.Add(new MessageItem
                {
                    From = Username,
                    Text = $"[ARQUIVO] Enviado: {Path.GetFileName(dlg.FileName)}",
                    IsMine = true,
                    IsFile = true,
                    FilePath = dlg.FileName,
                    FileName = Path.GetFileName(dlg.FileName),
                }, isActive: true);
            }
        }
        catch (Exception ex)
        {
            Info = "[Arquivo] Falha ao enviar: " + ex.Message;
        }
    }

    // --- NOVOS MÉTODOS PARA HISTÓRICO E STATUS DE LEITURA ---

    private async Task SyncHistoryAsync(Conversation conv)
    {
        try
        {
            var p1 = Username;
            var p2 = conv.Id; 
            var sala = string.CompareOrdinal(p1, p2) < 0 ? $"{p1}:{p2}" : $"{p2}:{p1}";
            var conversaId = StringToGuid(sala);
            var myId = StringToGuid(Username);

            using var http = new HttpClient();
            http.BaseAddress = new Uri("http://localhost:5082");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);

            var url = $"/v1/conversations/{conversaId}/messages?limit=50";
            var res = await http.GetAsync(url);
            
            if (!res.IsSuccessStatusCode) return;

            var json = await res.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<ApiMessageResponse>(json);

            if (data?.items is null) return;

            foreach (var msg in data.items.OrderBy(x => x.criadoEm))
            {
                string texto = "";
                try { texto = msg.conteudo.GetProperty("text").GetString() ?? ""; } 
                catch { texto = "[Arquivo/Complexo]"; }

                bool isMine = (msg.usuarioRemetenteId == myId);
                
                var exists = conv.Messages.Any(m => m.Text == texto && Math.Abs((m.Timestamp - msg.criadoEm).TotalSeconds) < 2);

                if (!exists)
                {
                    conv.Add(new MessageItem
                    {
                        From = isMine ? Username : conv.Id,
                        Text = texto,
                        IsMine = isMine,
                        Timestamp = msg.criadoEm.ToLocalTime()
                    }, isActive: Selected == conv);
                }
            }
        }
        catch (Exception ex)
        {
            Info = $"[Sync] Falha: {ex.Message}";
        }
    }

    private async Task MarkAsReadAsync(string conversaNome)
    {
        try {
            var convId = StringToGuid(conversaNome);
            using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5082") };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _jwtToken);
            
            await http.PostAsync($"/v1/conversations/{convId}/read", null);
        } catch { /* ignora erro de read receipt */ }
    }

    private string GenerateDevToken(string username)
    {
        var userId = StringToGuid(username);
        var tenantId = StringToGuid("default-org");

        var issuer = "Whatslike";
        var audience = "Whatslike.Clients";
        var secret = "26c8d9a793975af4999bc048990f6fd1"; 
        
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, username),
            new Claim(JwtRegisteredClaimNames.Name, username),
            new Claim("tenant_id", tenantId.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: DateTime.UtcNow.AddYears(10),
            signingCredentials: creds
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static Guid StringToGuid(string value)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
        return new Guid(hash);
    }
}

public class ApiMessageResponse
{
    public List<ApiMessageItem> items { get; set; } = new();
}

public class ApiMessageItem
{
    public string direcao { get; set; } = string.Empty; 
    public Guid usuarioRemetenteId { get; set; }
    public JsonElement conteudo { get; set; }
    public DateTime criadoEm { get; set; }
}
