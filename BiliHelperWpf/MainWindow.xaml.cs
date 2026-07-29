using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            new Uri("pack://application:,,,/Assets/logo.jpg"));

        // 监听窗口状态变化，切换最大化/还原图标
        StateChanged += OnWindowStateChanged;

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

    /// <summary>
    /// 点击遮罩关闭历史抽屉。
    /// </summary>
    private void HistoryOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _vm.ToggleHistoryCommand.Execute(null);
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
}
