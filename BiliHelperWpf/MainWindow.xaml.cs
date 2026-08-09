using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using BiliHelperWpf.ViewModels;

namespace BiliHelperWpf;

public partial class MainWindow : Window
{
    private MainViewModel _vm = null!;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
        _vm = (MainViewModel)DataContext;

        // 设置任务栏图标
        Icon = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/logo.png"));

        // Windows 11 DWM 原生圆角（句柄可用后生效）
        SourceInitialized += (_, _) => ApplyWindowCornerRoundness();

        // 监听窗口状态变化，切换最大化/还原图标
        StateChanged += OnWindowStateChanged;

        // 最大化/还原时同步圆角/直角
        StateChanged += (_, _) => OnWindowStateChangedForCorner();

        // 切换分P时自动滚动字幕列表到顶部
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedPart))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (SubtitleListBox?.IsLoaded == true)
                    {
                        var scrollViewer = WpfHelper.FindVisualChild<ScrollViewer>(SubtitleListBox);
                        scrollViewer?.ScrollToTop();
                    }
                });
            }

            // 历史抽屉打开时播放滑入动画
            if (e.PropertyName == nameof(MainViewModel.IsHistoryOpen))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (HistoryDrawer?.RenderTransform is TranslateTransform tt)
                    {
                        if (_vm.IsHistoryOpen)
                        {
                            tt.X = 320;
                            var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200))
                            {
                                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                            };
                            tt.BeginAnimation(TranslateTransform.XProperty, anim);
                        }
                        else
                        {
                            var anim = new DoubleAnimation(320, TimeSpan.FromMilliseconds(150))
                            {
                                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                            };
                            tt.BeginAnimation(TranslateTransform.XProperty, anim);
                        }
                    }
                });
            }

        };
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 启动时探测 cookie 状态；无 cookie 时首次自动弹设置中心的账号面板
        bool valid = await _vm.RefreshCookieStatusAsync();
        if (!valid)
            OpenSettingsWindow(0);
    }

    /// <summary>
    /// 点击遮罩关闭历史抽屉（仅当点击的是遮罩本身，不是子元素）。
    /// </summary>
    private void HistoryOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只有直接点击 Rectangle 遮罩才关闭，点击按钮等子元素不触发
        if (e.OriginalSource is Rectangle)
            _vm.ToggleHistoryCommand.Execute(null);
    }

    /// <summary>
    /// 切换字幕 TAB（原始字幕 / 原始全文）。
    /// </summary>
    private void SubtitleTab_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border border)
        {
            if (border.Name == "TabOriginal")
                _vm.SelectedSubtitleTab = MainViewModel.TabOriginal;
            else if (border.Name == "TabFullText")
                _vm.SelectedSubtitleTab = MainViewModel.TabFullText;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            MaxIcon.Visibility = Visibility.Collapsed;
            RestoreIcon.Visibility = Visibility.Visible;
        }
        else
        {
            MaxIcon.Visibility = Visibility.Visible;
            RestoreIcon.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 标题栏双击最大化/还原（拖拽由 WindowChrome 自动处理）
    /// </summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>
    /// 打开设置中心。
    /// </summary>
    private SettingsWindow? _settingsWindow;

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        OpenSettingsWindow();
    }

    private void OpenSettingsWindow(int panelIndex = 0)
    {
        if (_settingsWindow != null && _settingsWindow.IsVisible)
        {
            _settingsWindow.NavigateTo(panelIndex);
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_vm, panelIndex) { Owner = this };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    // ── Windows 11 DWM 原生圆角 ─────────────────────────────────
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private enum DWM_WINDOW_CORNER_PREFERENCE
    {
        DWMWCP_DEFAULT = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND = 2,
        DWMWCP_ROUNDSMALL = 3,
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attr,
        ref int attrValue,
        int attrSize);

    /// <summary>
    /// 应用系统原生圆角（仅 Windows 11 生效，Windows 10 及以下自动忽略）。
    /// 最大化时恢复直角，还原时恢复圆角。
    /// </summary>
    private void ApplyWindowCornerRoundness()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            // 最大化时不圆角（系统行为），还原/普通态圆角
            var preference = WindowState == WindowState.Maximized
                ? (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND
                : (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND;

            DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                sizeof(int));
        }
        catch
        {
            // 非 Windows 11 / 平台不支持时静默忽略
        }
    }

    /// <summary>
    /// 窗口状态变化时同步圆角/直角（最大化直角，还原圆角）。
    /// </summary>
    private void OnWindowStateChangedForCorner()
    {
        if (new WindowInteropHelper(this).Handle != IntPtr.Zero)
            ApplyWindowCornerRoundness();
    }

}
