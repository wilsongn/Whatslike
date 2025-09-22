using System.Windows;

namespace Chat.Client.Wpf.Services;

public sealed class DialogService : IDialogService
{
    public string? Prompt(string title, string message, string? defaultValue = null)
    {
        var dlg = new InputDialog(title, message, defaultValue)
        {
            Owner = Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true ? dlg.InputText : null;
    }
}
