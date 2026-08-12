using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace BiliHelperWpf.Converters;

/// <summary>
/// 将 status 字符串 ("ok", "partial", "empty") 映射为颜色画刷。
/// 从当前主题资源字典查找画刷，随主题（浅/深）切换而变化。
/// ok → SystemFillColorSuccessBrush, partial → SystemFillColorAttentionBrush, empty → SystemFillColorNeutralBrush。
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush DefaultBrush =
        CreateBrush(Color.FromRgb(0x86, 0x90, 0x9C));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            string s when s.Equals("ok", StringComparison.OrdinalIgnoreCase) => "SystemFillColorSuccessBrush",
            string s when s.Equals("partial", StringComparison.OrdinalIgnoreCase) => "SystemFillColorAttentionBrush",
            string s when s.Equals("empty", StringComparison.OrdinalIgnoreCase) => "SystemFillColorNeutralBrush",
            _ => null,
        };

        if (key != null && Application.Current?.TryFindResource(key) is Brush brush)
            return brush;

        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var b = new SolidColorBrush(color);
        b.Freeze();
        return b;
    }
}
