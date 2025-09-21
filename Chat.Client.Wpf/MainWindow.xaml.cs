using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Win32;

namespace Chat.Client.Wpf;

public partial class MainWindow : Window
{
    private readonly SocketClient _client = new();
    public ObservableCollection<string> Messages { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        _client.OnMessage += AddLine;
        _client.OnInfo += AddLine;
        _client.OnError += AddLine;
        _client.OnUsers += users => AddLine("[Users] " + string.Join(", ", users));
        _client.OnProgress += (sent, total) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (total > 0)
                {
                    Prg.Maximum = total;
                    Prg.Value = sent;
                    var pct = (double)sent / total * 100;
                    LblPrg.Text = $"{pct:0.0}%";
                }
            });
        };
        _client.OnFileSaved += path => AddLine("[File] salvo em: " + path);
    }

    private async void BtnConnect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string host = TxtHost.Text.Trim();
            int port = int.Parse(TxtPort.Text.Trim());
            string user = TxtUser.Text.Trim();

            await _client.ConnectAsync(host, port, user);
            AddLine($"[OK] Conectado como {user} em {host}:{port}");
        }
        catch (Exception ex)
        {
            AddLine("[Erro] " + ex.Message);
        }
    }

    private async void BtnUsers_Click(object sender, RoutedEventArgs e)
    {
        await _client.ListUsersAsync();
    }

    private async void BtnCreateGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = TxtTarget.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { AddLine("[Info] informe o nome do grupo"); return; }
        await _client.CreateGroupAsync(name);
    }

    private async void BtnAddToGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = TxtTarget.Text.Trim();
        var user = TxtUser.Text.Trim(); // opcional: peça outro textbox pra escolher usuário
        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(user))
        {
            AddLine("[Info] informe grupo e usuário");
            return;
        }
        await _client.AddToGroupAsync(group, user);
    }

    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        var text = TxtInput.Text;
        TxtInput.Clear();

        var target = TxtTarget.Text.Trim();
        var isGroup = (CmbTargetType.SelectedIndex == 1);

        if (string.IsNullOrWhiteSpace(target))
        {
            AddLine("[Info] preencha o destino (user ou grupo)");
            return;
        }

        if (isGroup) await _client.SendGroupTextAsync(target, text);
        else await _client.SendPrivateTextAsync(target, text);
    }

    private async void BtnAttach_Click(object sender, RoutedEventArgs e)
    {
        var target = TxtTarget.Text.Trim();
        var isGroup = (CmbTargetType.SelectedIndex == 1);
        if (string.IsNullOrWhiteSpace(target)) { AddLine("[Info] preencha o destino"); return; }

        var dlg = new OpenFileDialog
        {
            Title = "Selecione o arquivo",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
        {
            if (isGroup) await _client.SendFileAsync(target, dlg.FileName);
            else await _client.SendFileAsync(target, dlg.FileName);
        }
    }

    private void AddLine(string line)
    {
        Dispatcher.Invoke(() => Messages.Add(line));
    }
}
