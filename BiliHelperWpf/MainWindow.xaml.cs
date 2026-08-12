using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using BiliHelperWpf.ViewModels;

namespace BiliHelperWpf;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
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

            // 历史抽屉打开/关闭：播放滑入动画 + 用 IsHitTestVisible=false 解除 TitleBar 的
            // WM_NCHITTEST → HTCAPTION 拖动劫持（WPF-UI 的 IsMouseOverElement 会检查
            // IsHitTestVisible，设为 false 后劫持失效，抽屉右上角 ✕ 可正常点击）；
            // 同时隐藏窗口按钮。TitleBar 保持 Visible 不折叠、不做位置变更 → 点击抽屉 ✕
            // 关闭时鼠标下方不会凭空出现窗口按钮，杜绝误触退出。
            if (e.PropertyName == nameof(MainViewModel.IsHistoryOpen))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    MainWindowTitleBar.IsHitTestVisible = !_vm.IsHistoryOpen;
                    MainWindowTitleBar.ShowClose = !_vm.IsHistoryOpen;
                    MainWindowTitleBar.ShowMaximize = !_vm.IsHistoryOpen;
                    MainWindowTitleBar.ShowMinimize = !_vm.IsHistoryOpen;

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

}
