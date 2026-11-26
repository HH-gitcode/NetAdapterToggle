using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NetAdapterToggle.Models;
using Color = System.Windows.Media.Color;

namespace NetAdapterToggle.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class AdapterStatusBrushConverter : IValueConverter
{
    public string Mode { get; set; } = "BadgeBackground";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AdapterInfo adapter)
            return System.Windows.Media.Brushes.Transparent;

        // 3 states: Connected (Green), EnabledDisconnected (Amber/Blue), Disabled (Slate/Red)
        if (!adapter.IsEnabled)
        {
            return Mode switch
            {
                "BadgeBackground" => new SolidColorBrush(Color.FromRgb(254, 226, 226)),   // #FEE2E2
                "BadgeForeground" => new SolidColorBrush(Color.FromRgb(185, 28, 28)),    // #B91C1C
                "IconBackground"  => new SolidColorBrush(Color.FromRgb(241, 245, 249)),  // #F1F5F9
                "IconForeground"  => new SolidColorBrush(Color.FromRgb(148, 163, 184)),  // #94A3B8
                "ActionBackground" => new SolidColorBrush(Color.FromRgb(22, 163, 74)),   // #16A34A (Enable)
                "ActionForeground" => new SolidColorBrush(Colors.White),
                "ActionBorder"     => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
                _ => System.Windows.Media.Brushes.Transparent
            };
        }

        if (adapter.IsConnected)
        {
            return Mode switch
            {
                "BadgeBackground" => new SolidColorBrush(Color.FromRgb(220, 252, 231)),   // #DCFCE7
                "BadgeForeground" => new SolidColorBrush(Color.FromRgb(21, 128, 61)),    // #15803D
                "IconBackground"  => new SolidColorBrush(Color.FromRgb(219, 234, 254)),  // #DBEAFE
                "IconForeground"  => new SolidColorBrush(Color.FromRgb(37, 99, 235)),    // #2563EB
                "ActionBackground" => new SolidColorBrush(Color.FromRgb(254, 242, 242)), // #FEF2F2 (Disable)
                "ActionForeground" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),   // #DC2626
                "ActionBorder"     => new SolidColorBrush(Color.FromRgb(254, 202, 202)), // #FECACA
                _ => System.Windows.Media.Brushes.Transparent
            };
        }

        // Enabled but disconnected
        return Mode switch
        {
            "BadgeBackground" => new SolidColorBrush(Color.FromRgb(254, 243, 199)),   // #FEF3C7
            "BadgeForeground" => new SolidColorBrush(Color.FromRgb(180, 83, 9)),     // #B45309
            "IconBackground"  => new SolidColorBrush(Color.FromRgb(224, 242, 254)),  // #E0F2FE
            "IconForeground"  => new SolidColorBrush(Color.FromRgb(2, 132, 199)),    // #0284C7
            "ActionBackground" => new SolidColorBrush(Color.FromRgb(254, 242, 242)), // #FEF2F2 (Disable)
            "ActionForeground" => new SolidColorBrush(Color.FromRgb(220, 38, 38)),   // #DC2626
            "ActionBorder"     => new SolidColorBrush(Color.FromRgb(254, 202, 202)), // #FECACA
            _ => System.Windows.Media.Brushes.Transparent
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
