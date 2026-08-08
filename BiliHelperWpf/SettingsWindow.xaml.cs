using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BiliHelperWpf.Models;
using BiliHelperWpf.Services;

namespace BiliHelperWpf;

public partial class SettingsWindow : Window
{
    private readonly AiReadService _aiReadService = new();
    private bool _isTesting;

    public SettingsWindow()
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
            ? (Brush)FindResource("SuccessBrush")
            : (Brush)FindResource("ErrorBrush");
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
            TestButton.Content = "🔌 测试连通性";
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        AiSettingsStore.Save(CollectSettings());
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }
}