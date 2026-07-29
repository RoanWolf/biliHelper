using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BiliHelperWpf.Converters;

/// <summary>
/// true → Visible, false → Collapsed
/// 可选 Inverted（true → Collapsed, false → Visible）
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public bool Inverted { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool bv && bv;
        if (Inverted) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Visibility v)
        {
            bool result = v == Visibility.Visible;
            return Inverted ? !result : result;
        }
        return false;
    }
}
