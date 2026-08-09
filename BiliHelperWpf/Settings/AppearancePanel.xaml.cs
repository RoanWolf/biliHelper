using System.Windows;
using System.Windows.Controls;

namespace BiliHelperWpf.Settings;

/// <summary>
/// 外观面板：浅色 / 深色主题选择。业务逻辑复用 ThemeManager。
/// </summary>
public partial class AppearancePanel : UserControl
{
    public AppearancePanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DarkRadio.IsChecked == true || LightRadio.IsChecked == true)
                return;
            // 打开面板时同步当前主题
            if (ThemeManager.IsDark) DarkRadio.IsChecked = true;
            else LightRadio.IsChecked = true;
        };
    }

    private void ThemeRadio_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender == LightRadio)
            ThemeManager.ApplyDark(false);
        else if (sender == DarkRadio)
            ThemeManager.ApplyDark(true);
    }
}
