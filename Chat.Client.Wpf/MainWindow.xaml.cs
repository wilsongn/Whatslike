using System.Windows;
using Chat.Client.Wpf.Services;
using Chat.Client.Wpf.ViewModels;

namespace Chat.Client.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new SocketChatClient(), new DialogService());
    }
}
