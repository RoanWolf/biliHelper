using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace BiliHelperWpf;

public partial class App : Application
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "BiliHelperWpf_Log.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        // 每次启动覆盖旧日志
        try { File.WriteAllText(LogPath, ""); } catch { }
        Log("=== 应用程序启动 ===");
        Log($"工作目录: {Environment.CurrentDirectory}");
        Log($"程序路径: {Environment.ProcessPath}");

        // UI 线程未捕获异常
        DispatcherUnhandledException += (_, args) =>
        {
            Log($"[FATAL] UI 异常: {args.Exception}");
            WriteToFile();
            MessageBox.Show(
                $"发生未处理的 UI 异常：\n{args.Exception.Message}",
                "BiliHelper - 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        // 非 UI 线程未捕获异常
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log($"[FATAL] AppDomain 异常: {ex}");
            WriteToFile();
            MessageBox.Show(
                $"发生未处理的异常：\n{ex?.Message ?? "未知错误"}",
                "BiliHelper - 致命错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        // 任务调度器异常
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log($"[WARN] 后台任务异常: {args.Exception}");
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log("=== 应用程序退出 ===");
        WriteToFile();
        base.OnExit(e);
    }

    internal static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
            System.Diagnostics.Debug.WriteLine(line);
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // 日志写入失败不崩溃
        }
    }

    internal static void WriteToFile()
    {
        try
        {
            File.AppendAllText(LogPath, Environment.NewLine);
        }
        catch { }
    }
}
