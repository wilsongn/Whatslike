namespace Chat.Client.Wpf.Services;

public interface IDialogService
{
    string? Prompt(string title, string message, string? defaultValue = null);
}
