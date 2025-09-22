using System.Windows;

namespace Chat.Client.Wpf
{
    public partial class InputDialog : Window
    {
        public string Message { get; private set; } = "";
        public string? InputText { get; set; }

        public InputDialog(string title, string message, string? defaultValue = null)
        {
            InitializeComponent();          // <- precisa compilar o XAML (Build Action = Page)
            Title = title;
            Message = message;
            InputText = defaultValue ?? string.Empty;
            DataContext = this;

            Loaded += (_, __) => { TxtInput.Focus(); TxtInput.SelectAll(); };
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(InputText))
            {
                MessageBox.Show(this, "Digite um valor.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
