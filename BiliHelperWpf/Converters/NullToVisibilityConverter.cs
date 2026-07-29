using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BiliHelperWpf.Converters;

/// <summary>
/// null → Collapsed, non-null → Visible
/// 传参 "Invert" 则反转：null → Visible, non-null → Collapsed
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase);
        bool isNull = value is null;
        bool visible = invert ? isNull : !isNull;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
