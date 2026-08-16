using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BiliHelperWpf.Models;
using BiliHelperWpf.Services;

namespace BiliHelperWpf.Settings;

/// <summary>
/// 飞书同步面板：启用开关 + App ID/Secret/群号/根文件夹 + 连通测试。
/// 业务逻辑复用 FeishuSettingsStore / FeishuService.TestAsync。
/// </summary>
public partial class FeishuPanel : UserControl
{
    private readonly FeishuService _feishuService = new();
    private bool _isTesting;

    public FeishuPanel()
    {
        InitializeComponent();
        LoadExisting();
    }

    private void LoadExisting()
    {
        var s = FeishuSettingsStore.Load();
        EnableSwitch.IsChecked = s.Enabled;
        AppIdBox.Text = s.AppId;
        if (!string.IsNullOrEmpty(s.AppSecret))
            AppSecretBox.Password = s.AppSecret;
        ChatIdBox.Text = s.ChatId;
        RootFolderBox.Text = s.RootFolder;
        UpdateFieldEnable();
    }

    private FeishuSettings CollectSettings() => new()
    {
        Enabled = EnableSwitch.IsChecked == true,
        AppId = AppIdBox.Text?.Trim() ?? "",
        AppSecret = AppSecretBox.Password?.Trim() ?? "",
        ChatId = ChatIdBox.Text?.Trim() ?? "",
        RootFolder = RootFolderBox.Text?.Trim() ?? "",
    };

    /// <summary>开关状态变化：同步字段可用性。</summary>
    private void EnableSwitch_IsCheckedChanged(object sender, RoutedEventArgs e)
    {
        UpdateFieldEnable();
    }

    private void UpdateFieldEnable()
    {
        bool on = EnableSwitch.IsChecked == true;
        AppIdBox.IsEnabled = on;
        AppSecretBox.IsEnabled = on;
        ChatIdBox.IsEnabled = on;
        RootFolderBox.IsEnabled = on;
    }

    private void SetTestResult(bool ok, string message)
    {
        TestResultBox.Visibility = Visibility.Visible;
        TestResultText.Text = $"{(ok ? "✔" : "✖")} {message}";
        TestResultText.Foreground = ok
            ? (Brush)FindResource("SystemFillColorSuccessBrush")
            : (Brush)FindResource("SystemFillColorCriticalBrush");
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isTesting) return;
        _isTesting = true;
        TestButton.IsEnabled = false;
        TestButton.Content = "测试中...";
        TestResultBox.Visibility = Visibility.Collapsed;

        try
        {
            var settings = CollectSettings();
            var (ok, message) = await Task.Run(() => _feishuService.TestAsync(settings));
            SetTestResult(ok, message);
        }
        catch (OperationCanceledException)
        {
            SetTestResult(false, "测试已取消");
        }
        catch (Exception ex)
        {
            SetTestResult(false, $"测试异常: {ex.Message}");
        }
        finally
        {
            _isTesting = false;
            TestButton.IsEnabled = true;
            TestButton.Content = "测试";
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        FeishuSettingsStore.Save(CollectSettings());
        // 保存后同步主界面状态（设置生效无需重启）
        App.Log($"FeishuPanel: 设置已保存 enabled={EnableSwitch.IsChecked == true}");
    }
}
