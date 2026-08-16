using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliHelperWpf.Models;

namespace BiliHelperWpf.Services;

/// <summary>
/// 飞书同步服务：spawn feishu.py 子进程（test 连通测试 / sync 同步分P），
/// 逐行读取 stdout JSONL。凭证通过环境变量 FEISHU_* 注入（非空才注入）。
///
/// 子进程契约（详见 BiliHelperCore/feishu.py）:
///   test: {"type":"complete","ok":true,"message":...} / {"type":"error","message":...}
///   sync: {"type":"status","step":...} … {"type":"complete","document_url":...} / {"type":"error","message":...}
/// </summary>
public class FeishuService
{
    private static readonly string RepoRoot = FindDirContaining("bilihelperCore")
        ?? AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string CoreDir = Path.Combine(RepoRoot, "bilihelperCore");
    private static readonly string FeishuScript = Path.Combine(CoreDir, "feishu.py");
    private static readonly string PythonExe =
        Path.Combine(CoreDir, ".venv", "Scripts", "python.exe");

    /// <summary>把飞书设置注入子进程环境变量（非空才注入，Python 端回退默认）。</summary>
    private static void ApplySettings(ProcessStartInfo psi, FeishuSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.AppId))
            psi.EnvironmentVariables["FEISHU_APP_ID"] = settings.AppId.Trim();
        if (!string.IsNullOrWhiteSpace(settings.AppSecret))
            psi.EnvironmentVariables["FEISHU_APP_SECRET"] = settings.AppSecret.Trim();
        if (!string.IsNullOrWhiteSpace(settings.ChatId))
            psi.EnvironmentVariables["FEISHU_CHAT_ID"] = settings.ChatId.Trim();
        if (!string.IsNullOrWhiteSpace(settings.RootFolder))
            psi.EnvironmentVariables["FEISHU_ROOT_FOLDER"] = settings.RootFolder.Trim();
    }

    private static ProcessStartInfo BuildPsi(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"\"{FeishuScript}\" {args}",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        // 铁律：所有 spawn 点一致（UTF-8 输出 + UTF-8 解码）
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    /// <summary>
    /// 连通性测试：spawn feishu.py test，返回 (ok, 分类提示)。
    /// 无论退出码都先解析 stdout JSONL 拿分类信息。
    /// </summary>
    public async Task<(bool ok, string message)> TestAsync(
        FeishuSettings settings, CancellationToken ct = default)
    {
        var psi = BuildPsi("test");
        ApplySettings(psi, settings);

        using var process = new Process { StartInfo = psi };
        process.Start();
        App.Log($"FeishuService: test 子进程已启动, PID={process.Id}");

        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardError.ReadLineAsync(ct) != null) { }
            }
            catch (OperationCanceledException) { }
        }, ct);

        string? lastLine = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await process.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                lastLine = line;
                App.Log($"FeishuService: test stdout {Trim(line)}");
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        if (lastLine != null)
        {
            try
            {
                using var doc = JsonDocument.Parse(lastLine);
                var root = doc.RootElement;
                if (root.TryGetProperty("ok", out var okElem))
                {
                    var message = root.TryGetProperty("message", out var msgElem)
                        ? msgElem.GetString() ?? ""
                        : "";
                    return (ok: okElem.GetBoolean(), message: message);
                }
                if (root.TryGetProperty("message", out var errElem))
                {
                    return (false, errElem.GetString() ?? "测试失败");
                }
            }
            catch (JsonException) { }
        }

        return (false, process.ExitCode == 0 ? "测试结果未知" : $"测试失败（退出码 {process.ExitCode}）");
    }

    /// <summary>
    /// 同步某分P 到飞书云文档：spawn feishu.py sync --bv-dir &lt;dir&gt; --part N。
    /// onStatus: 步骤进度；onComplete: 文档链接；onError: 失败消息。
    /// </summary>
    public async Task SyncPartAsync(
        string bvDir,
        int partNumber,
        FeishuSettings settings,
        Action<string>? onStatus = null,
        Action<string>? onComplete = null,
        Action<string>? onError = null,
        CancellationToken ct = default)
    {
        var psi = BuildPsi($"sync --bv-dir \"{bvDir}\" --part {partNumber}");
        ApplySettings(psi, settings);

        using var process = new Process { StartInfo = psi };
        process.Start();
        App.Log($"FeishuService: sync 子进程已启动, PID={process.Id}, part={partNumber}");

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? line = await process.StandardError.ReadLineAsync(ct);
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                        App.Log($"FeishuService: sync stderr {Trim(line)}");
                }
            }
            catch (OperationCanceledException) { }
        }, ct);

        string? lastError = null;
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await process.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                App.Log($"FeishuService: sync stdout {Trim(line)}");

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = root.GetProperty("type").GetString() ?? "";
                    switch (type)
                    {
                        case "status":
                            onStatus?.Invoke(root.TryGetProperty("step", out var s) ? s.GetString() ?? "" : "");
                            break;
                        case "complete":
                            var url = root.TryGetProperty("document_url", out var u)
                                ? u.GetString() ?? ""
                                : "";
                            onComplete?.Invoke(url);
                            return;
                        case "error":
                            lastError = root.TryGetProperty("message", out var m)
                                ? m.GetString() ?? "同步失败"
                                : "同步失败";
                            break;
                    }
                }
                catch (JsonException ex)
                {
                    App.Log($"FeishuService: JSON 解析失败: {ex.Message}, 行: {Trim(line)}");
                }
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            App.Log("FeishuService: sync 被取消，终止子进程");
            KillProcess(process);
            throw;
        }

        onError?.Invoke(lastError ?? (process.ExitCode == 0 ? "同步未返回结果" : $"同步失败（退出码 {process.ExitCode}）"));
    }

    private static void KillProcess(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch { /* 进程可能已退出 */ }
    }

    private static string Trim(string line) =>
        line.Length > 300 ? line[..300] + "..." : line;

    private static string? FindDirContaining(string dirName)
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 5; i++)
        {
            if (dir == null) break;
            if (Directory.Exists(Path.Combine(dir.FullName, dirName)))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
