using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Chat.Client.Wpf;

public sealed class UnreadToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is int i && i > 0) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class PercentToDouble : IValueConverter
{
    // Converte "12.3%" (string) em 0.123 (double) para o ProgressBar (Max=1)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && s.EndsWith("%") && double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.CurrentCulture, out var d))
            return d / 100.0;
        return 0.0;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
