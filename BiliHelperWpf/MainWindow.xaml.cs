using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
                // 等 UI 更新完再滚动到顶部
                Dispatcher.BeginInvoke(() =>
                {
                    if (SubtitleListBox?.IsLoaded == true)
                    {
                        var scrollViewer = WpfHelper.FindVisualChild<ScrollViewer>(SubtitleListBox);
                        scrollViewer?.ScrollToTop();
                    }
                });
            }
        };
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
