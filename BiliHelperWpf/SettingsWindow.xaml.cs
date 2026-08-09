using System.Windows;
using System.Windows.Controls;
using BiliHelperWpf.Settings;
using BiliHelperWpf.ViewModels;

namespace BiliHelperWpf;

/// <summary>
/// 设置中心（⚙️）：Fluent 风格侧边分组导航 + 内嵌面板。
/// 导航分组：「服务」= 获取cookie / AI模型，「个性化」= 外观。
/// 由 MainWindow 打开（SettingsButton_Click / 无 cookie 时自动弹账号面板）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly UserControl[] _panels;

    public SettingsWindow(MainViewModel vm, int panelIndex = 0)
    {
        InitializeComponent();
        _vm = vm;

        _panels =
        [
            new AccountPanel(vm),
            new AiModelPanel(),
            new AppearancePanel(),
        ];

        NavigateTo(panelIndex);
    }

    /// <summary>切换到指定面板（供外部触发，如无 cookie 自动定位到账号面板）。</summary>
    public void NavigateTo(int panelIndex)
    {
        if (panelIndex < 0 || panelIndex >= _panels.Length) panelIndex = 0;

        // 直接设置内容：XAML 默认选中项（NavAccount）不会触发 Checked 事件，
        // 必须先赋值再同步选中态，避免内容区空白。
        ContentHost.Content = _panels[panelIndex];

        switch (panelIndex)
        {
            case 0 when NavAccount.IsChecked != true: NavAccount.IsChecked = true; break;
            case 1 when NavAi.IsChecked != true: NavAi.IsChecked = true; break;
            case 2 when NavAppearance.IsChecked != true: NavAppearance.IsChecked = true; break;
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // RadioButton 的 Tag 存面板下标；同一 GroupName 下换选中会触发本事件。
        // 注意：XAML 加载阶段（InitializeComponent）也会触发本事件，此时 _panels 尚未初始化，
        // 必须做空值防御（日志曾报 NullReferenceException → SettingsWindow.xaml.cs:54）。
        if (sender is not RadioButton { Tag: string tag }) return;
        if (_panels is null) return;
        if (int.TryParse(tag, out var index) && index >= 0 && index < _panels.Length)
            ContentHost.Content = _panels[index];
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
