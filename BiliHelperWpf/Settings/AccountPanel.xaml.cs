using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using BiliHelperWpf.Models;
using BiliHelperWpf.ViewModels;

namespace BiliHelperWpf.Settings;

/// <summary>
/// 账号面板：未登录时内嵌扫码登录，已登录时显示状态 + 退出。
/// 复用 MainViewModel.LoginAsync / DeleteCookiesAsync 的业务逻辑。
/// 轮询生命周期挂在 Loaded/Unloaded 上（切导航 / 关窗时自动取消）。
/// </summary>
public partial class AccountPanel : UserControl
{
    private readonly MainViewModel _vm;
    private CancellationTokenSource? _cts;
    private bool _isPolling;

    // 单调递增的登录轮次：防止旧的 RunLoginAsync 完成回调干扰最新一次。
    private int _runId;

    public AccountPanel(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        Loaded += (_, _) =>
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            RefreshView();
        };
        Unloaded += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            StopPolling();
        };
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CookieState))
            Dispatcher.BeginInvoke(RefreshView);
    }

    /// <summary>根据当前登录态切换 已登录 / 扫码 视图。</summary>
    private void RefreshView()
    {
        bool loggedIn = _vm.CookieState == CookieState.Valid;
        LoggedInPanel.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        LoginPanel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;

        if (loggedIn)
        {
            StatusInfo.Text = string.IsNullOrEmpty(_vm.CookieTooltip) ? "B 站已登录" : _vm.CookieTooltip;
            StopPolling();
        }
        else if (!_isPolling)
        {
            _ = StartLoginAsync();
        }
    }

    /// <summary>
    /// 启动新一轮登录轮询。自动取消上一轮，保证同一时刻只有一个轮询存活。
    /// </summary>
    private async Task StartLoginAsync()
    {
        int myRun = ++_runId;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _isPolling = true;
        var ct = _cts.Token;

        RegenButton.IsEnabled = false;
        StatusText.Text = "正在生成二维码...";
        QrImage.Source = null;

        bool success = false;
        string? error = null;
        try
        {
            success = await _vm.LoginAsync(
                onQr: path => Dispatcher.Invoke(() => ShowQr(path)),
                onStatus: code => Dispatcher.Invoke(() => UpdateStatus(code)),
                onError: msg => Dispatcher.Invoke(() => ShowError(msg)),
                ct);
        }
        catch (OperationCanceledException)
        {
            // 被新一轮取消或面板卸载，忽略
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // 只有最新一轮才更新 UI，避免过期回调误操作
        if (myRun != _runId)
            return;

        _isPolling = false;
        if (success)
        {
            StatusText.Text = "登录成功";
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

    private async void RegenButton_Click(object sender, RoutedEventArgs e)
    {
        await StartLoginAsync();
    }

    private async void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        await _vm.DeleteCookiesAsync();
        RefreshView();
    }

    private void StopPolling()
    {
        _runId++;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _isPolling = false;
    }
}
