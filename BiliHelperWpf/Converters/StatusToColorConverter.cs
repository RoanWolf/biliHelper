using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BiliHelperWpf.Converters;

/// <summary>
/// 将 status 字符串 ("ok", "partial", "empty") 映射为颜色画刷。
/// ok → 成功 #00B42A, partial → 运行 #165DFF, empty → 等待 #86909C
/// </summary>
public class StatusToColorConverter : IValueConverter
{
    private static readonly SolidColorBrush OkBrush = new(Color.FromRgb(0x00, 0xB4, 0x2A));
    private static readonly SolidColorBrush PartialBrush = new(Color.FromRgb(0x16, 0x5D, 0xFF));
    private static readonly SolidColorBrush EmptyBrush = new(Color.FromRgb(0x86, 0x90, 0x9C));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(0x86, 0x90, 0x9C));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string status)
        {
            return status.ToLowerInvariant() switch
            {
                "ok" => OkBrush,
                "partial" => PartialBrush,
                "empty" => EmptyBrush,
                _ => DefaultBrush
            };
        }
        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
