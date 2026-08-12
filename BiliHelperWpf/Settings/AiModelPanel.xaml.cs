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
/// AI 大模型连接面板：API Key / base_url / model 配置 + 连通性测试。
/// 业务逻辑复用 AiSettingsStore / AiReadService.TestConnectivityAsync。
/// </summary>
public partial class AiModelPanel : UserControl
{
    private readonly AiReadService _aiReadService = new();
    private bool _isTesting;

    public AiModelPanel()
    {
        InitializeComponent();
        LoadExisting();
    }

    private void LoadExisting()
    {
        var s = AiSettingsStore.Load();
        if (!string.IsNullOrEmpty(s.ApiKey))
            ApiKeyBox.Password = s.ApiKey;
        BaseUrlBox.Text = s.BaseUrl;
        ModelBox.Text = s.Model;
    }

    private AiSettings CollectSettings() => new()
    {
        ApiKey = ApiKeyBox.Password?.Trim() ?? "",
        BaseUrl = BaseUrlBox.Text?.Trim() ?? "",
        Model = ModelBox.Text?.Trim() ?? "",
    };

    private void SetTestResult(bool ok, string message)
    {
        TestResultBox.Visibility = Visibility.Visible;
        TestResultText.Text = $"{((ok ? "✔" : "✖"))} {message}";
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
            var (ok, message) = await Task.Run(() =>
                _aiReadService.TestConnectivityAsync(settings));
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
            TestButton.Content = "测试连通性";
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        AiSettingsStore.Save(CollectSettings());
    }
}
