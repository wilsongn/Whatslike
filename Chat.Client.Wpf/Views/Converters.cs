using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Chat.Client.Wpf;

public sealed class BoolToBrush : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool mine = value is bool b && b;
        return mine ? new SolidColorBrush(Color.FromRgb(215, 240, 255))
                    : new SolidColorBrush(Color.FromRgb(235, 235, 235));
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToAlignment : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool mine = value is bool b && b;
        return mine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
