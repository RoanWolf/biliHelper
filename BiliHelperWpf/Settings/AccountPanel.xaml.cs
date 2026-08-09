using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BiliHelperWpf.Models;
using BiliHelperWpf.Services;
using BiliHelperWpf.ViewModels;

namespace BiliHelperWpf.Settings;

/// <summary>
/// 账号面板：未登录时内嵌扫码登录，已登录时显示状态 + 退出。
/// 复用 MainViewModel.LoginAsync / DeleteCookiesAsync 的业务逻辑。
/// 轮询生命周期挂在 Loaded/Unloaded 上（切导航 / 关窗时自动取消）。
///
/// 并发安全：
/// - 登录流程由 <see cref="_loginGate"/> 互斥串行 ——「重新生成」会先取消上一轮，
///   并在 <see cref="StartLoginAsync"/> 中 await 旧任务彻底结束（含子进程 kill）
///   再启动新的，保证同一时刻只有一个 auth.py 子进程存活，杜绝并发写
///   cookies.json / cookies.txt 导致损坏。
/// - <see cref="_runId"/> 单调递增，防止旧轮次的回调 / UI 更新干扰最新一次。
/// </summary>
public partial class AccountPanel : UserControl
{
    private readonly MainViewModel _vm;

    // 用户信息读取（欢迎卡片昵称/头像/UID）
    private readonly AuthService _authService = new();

    // 登录互斥闸门：串行化所有登录轮次
    private readonly SemaphoreSlim _loginGate = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _loginTask;
    private bool _isPolling;

    // 单调递增的登录轮次：防止旧的登录回调干扰最新一次。
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
            _ = LoadUserInfoAsync();
        }
        else if (!_isPolling)
        {
            _ = StartLoginAsync();
        }
    }

    /// <summary>加载当前登录用户信息（昵称/头像/UID），填充欢迎卡片。</summary>
    private async Task LoadUserInfoAsync()
    {
        try
        {
            var ev = await _authService.GetUserAsync();

            if (!string.IsNullOrEmpty(ev.Uname))
                UserNameText.Text = ev.Uname;
            if (ev.Mid > 0)
                UserUidText.Text = $"UID: {ev.Mid}";

            // 头像：B 站 face 可能返回 http，统一转 https 避免加载受限；失败时显示占位图标
            if (!string.IsNullOrEmpty(ev.Face))
            {
                var face = ev.Face.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(face, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                UserAvatar.Source = bmp;
                AvatarFallback.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // 用户信息加载失败不影响已登录状态，静默降级为占位显示
        }
    }

    private void UserAvatar_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        UserAvatar.Source = null;
        AvatarFallback.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// 启动新一轮登录轮询。
    /// 先取消上一轮并等它（含旧子进程 kill）完全结束，再启动新的 —— 同一时刻
    /// 只有一个登录子进程存活，避免并发写 cookies 文件。
    /// </summary>
    private async Task StartLoginAsync()
    {
        int myRun = ++_runId;

        // 取消上一轮，并等待其彻底退出（LoginAsync 返回 = 子进程已结束/已 kill）
        _cts?.Cancel();
        Task? oldTask = _loginTask;
        if (oldTask is not null)
        {
            try { await oldTask; }
            catch (OperationCanceledException) { }
        }

        // 串行闸门：防止并发启动
        await _loginGate.WaitAsync();
        CancellationTokenSource? cts = null;
        try
        {
            // 等待期间若面板已卸载 / 被 StopPolling，则放弃本轮
            if (myRun != _runId)
                return;

            cts = new CancellationTokenSource();
            _cts = cts;
            _isPolling = true;
            var ct = cts.Token;

            RegenButton.IsEnabled = false;
            StatusText.Text = "正在生成二维码...";
            QrImage.Source = null;

            _loginTask = LoginCoreAsync(myRun, ct);
            await _loginTask;
        }
        finally
        {
            // 任务已 await 完成（含被取消/异常），dispose 安全；提前 return 时 cts 为 null
            cts?.Dispose();
            _loginGate.Release();
        }
    }

    /// <summary>实际执行一轮登录：调用 MainViewModel.LoginAsync 并处理结果 UI。</summary>
    private async Task LoginCoreAsync(int myRun, CancellationToken ct)
    {
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

    private void ShowQr(string imageBase64)
    {
        try
        {
            var bytes = Convert.FromBase64String(imageBase64);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            QrImage.Source = bmp;
            StatusText.Text = "等待扫码...";
            // 二维码已就绪，允许用户随时重新生成（StartLoginAsync 会先取消并等旧轮次彻底结束）
            RegenButton.IsEnabled = true;
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
            86038 => "二维码已过期，请重新生成",
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
        _cts = null;
        _isPolling = false;
    }
}
