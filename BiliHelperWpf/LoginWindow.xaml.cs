using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using BiliHelperWpf.Models;
using BiliHelperWpf.ViewModels;

namespace BiliHelperWpf;

public partial class LoginWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Action? _onClose;
    private CancellationTokenSource? _cts;
    private bool _closeNotified;

    // 单调递增的登录轮次：防止旧的 RunLoginAsync 完成回调干扰最新一次。
    private int _runId;

    public LoginWindow(MainViewModel vm, Window owner, Action? onClose = null)
    {
        InitializeComponent();
        _vm = vm;
        _onClose = onClose;
        if (owner != null)
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
    }

    private void NotifyClose()
    {
        if (_closeNotified) return;
        _closeNotified = true;
        _onClose?.Invoke();
    }

    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        await StartLoginAsync();
    }

    /// <summary>
    /// 启动新一轮登录轮询。自动取消上一轮，保证同一时刻只有一个轮询存活。
    /// </summary>
    private async Task StartLoginAsync()
    {
        int myRun = ++_runId;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        RegenButton.IsEnabled = false;
        StatusText.Text = "正在生成二维码...";
        QrImage.Source = null;

        bool success = false;
        string? error = null;
        try
        {
            success = await _vm.LoginAsync(
                onQr: imagePath => Dispatcher.Invoke(() => ShowQr(imagePath)),
                onStatus: code => Dispatcher.Invoke(() => UpdateStatus(code)),
                onError: msg => Dispatcher.Invoke(() => ShowError(msg)),
                ct);
        }
        catch (OperationCanceledException)
        {
            // 被新一轮取消或窗口关闭，忽略
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // 只有最新一轮才更新 UI / 关窗，避免过期回调误操作
        if (myRun != _runId)
            return;

        if (success)
        {
            StatusText.Text = "登录成功";
            NotifyClose();
            Close();
        }
        else if (!string.IsNullOrEmpty(error))
        {
            StatusText.Text = error;
            RegenButton.IsEnabled = true;
        }
        // 取消（非成功、无错误）时保持现状，等待重新生成
    }

    private void ShowQr(string imagePath)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(imagePath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            QrImage.Source = bmp;
            StatusText.Text = "等待扫码...";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"加载二维码失败: {ex.Message}";
            RegenButton.IsEnabled = true;
        }
    }

    private void UpdateStatus(int code)
    {
        StatusText.Text = code switch
        {
            86101 => "等待扫码...",
            86090 => "已扫码，请在手机上确认",
            _ => StatusText.Text,
        };
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        RegenButton.IsEnabled = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        NotifyClose();
        Close();
    }

    private void CloseXButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        NotifyClose();
        Close();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private async void RegenButton_Click(object sender, RoutedEventArgs e)
    {
        await StartLoginAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        // 让正在跑的轮询尽快收尾，且不让其再触碰 UI
        _runId++;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        NotifyClose();
        base.OnClosed(e);
    }
}