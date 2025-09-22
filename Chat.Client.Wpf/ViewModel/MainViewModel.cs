using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Chat.Client.Wpf.Infrastructure;
using Chat.Client.Wpf.Models;
using Chat.Client.Wpf.Services;
using Microsoft.Win32;

namespace Chat.Client.Wpf.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly ISocketChatClient _chat;
    private readonly IDialogService _dialog;

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
                await _chat.ConnectAsync(Host, Port, Username);
                Info = $"Conectado como {Username} em {Host}:{Port}";
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
            // param vem do item do ListBox quando chamado pelo menu de contexto;
            // se vier null (botão do header), usa o grupo selecionado.
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

    // --- Bindables (login/estado) ---
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

    // --- Conversas ---
    public ObservableCollection<Conversation> Directs { get; } = new();
    public ObservableCollection<Conversation> Groups { get; } = new();

    private Conversation? _selected;
    public Conversation? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value) && value is not null)
                value.Unread = 0;

            // Força atualização de CanExecute dos botões
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



    // helper para disparar PropertyChanged manualmente
    //protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // --- Composer / grupos ---
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

    private string _newGroup = "";
    public string NewGroup { get => _newGroup; set => SetProperty(ref _newGroup, value); }

    private string _newMember = "";
    public string NewMember { get => _newMember; set => SetProperty(ref _newMember, value); }

    // --- Commands ---
    public ICommand ConnectCommand { get; }
    public ICommand ListUsersCommand { get; }
    public ICommand CreateGroupCommand { get; }
    public ICommand AddMemberCommand { get; }
    public ICommand NewDirectCommand { get; }
    public ICommand NewGroupCommand { get; }
    public ICommand AddMemberToGroupCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand AttachCommand { get; }

    // --- Handlers ---
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
        // target pode ser user ou group (igual ao Selected.Id)
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
            // IsImage = IsImagePath(e.Path)        // se você tiver mantido o detector de imagem
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

        if (Selected.Type == ConversationType.Direct)
            await _chat.SendPrivateTextAsync(Selected.Id, text);
        else
            await _chat.SendGroupTextAsync(Selected.Id, text);

        Selected.Add(new MessageItem { From = Username, Text = text, IsMine = true }, isActive: true);
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
            else
            {
                Info = "Envio cancelado.";
            }
        }
        catch (Exception ex)
        {
            Info = "[Arquivo] Falha ao enviar: " + ex.Message;
        }
    }


}
