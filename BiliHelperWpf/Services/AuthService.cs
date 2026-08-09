using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliHelperWpf.Models;

namespace BiliHelperWpf.Services;

/// <summary>
/// B 站 cookie 登录/探测/续期服务。
/// 启动 Python 子进程 auth.py，逐行读取 stdout 的 JSONL。
/// </summary>
public class AuthService
{
    private static readonly string RepoRoot = FindDirContaining("bilihelperCore")
        ?? AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string CoreDir = Path.Combine(RepoRoot, "bilihelperCore");
    private static readonly string AuthScript = Path.Combine(CoreDir, "auth.py");
    private static readonly string PythonExe =
        Path.Combine(CoreDir, ".venv", "Scripts", "python.exe");

    /// <summary>
    /// 探测当前 cookie 状态。返回 (state, message)。
    /// state: none / valid / invalid。
    /// </summary>
    public async Task<(string state, string message)> CheckAsync(
        bool autoRefresh = false,
        CancellationToken ct = default)
    {
        var args = autoRefresh ? "check --refresh" : "check";
        var result = await RunSimpleAsync(args, ct);
        return (result.State ?? "invalid", result.Message ?? "");
    }

    /// <summary>
    /// 扫码登录。onQr 拿到二维码图片路径，onStatus 拿到轮询状态码，成功后 onSuccess。
    /// </summary>
    public async Task<bool> LoginAsync(
        Action<string> onQr,
        Action<int> onStatus,
        Action<int> onSuccess,
        Action<string> onError,
        CancellationToken ct = default)
    {
        var psi = BuildPsi("login");
        using var process = new Process { StartInfo = psi };
        process.Start();
        App.Log($"AuthService: 登录子进程已启动, PID={process.Id}");

        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? el = await process.StandardError.ReadLineAsync(ct);
                    if (el == null) break;
                    if (!string.IsNullOrWhiteSpace(el))
                        App.Log($"AuthService: login stderr {Trim(el)}");
                }
            }
            catch (OperationCanceledException) { }
        }, ct);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await process.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                App.Log($"AuthService: login stdout {Trim(line)}");

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString() ?? "";
                switch (type)
                {
                    case "qr":
                        onQr?.Invoke(root.GetProperty("image").GetString() ?? "");
                        break;
                    case "status":
                        onStatus?.Invoke(root.GetProperty("code").GetInt32());
                        break;
                    case "success":
                        onSuccess?.Invoke(root.GetProperty("count").GetInt32());
                        return true;
                    case "error":
                        onError?.Invoke(root.GetProperty("message").GetString() ?? "登录失败");
                        return false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            App.Log("AuthService: 登录读取被取消，终止子进程");
            KillProcess(process);
            throw;
        }
        catch (JsonException ex)
        {
            App.Log($"AuthService: JSON 解析失败: {ex.Message}");
            onError?.Invoke("登录响应解析失败");
            return false;
        }
        finally
        {
            try { await process.WaitForExitAsync(ct); } catch { }
            KillProcess(process);
        }

        return false;
    }

    /// <summary>
    /// 删除本地 cookie。
    /// </summary>
    public async Task<bool> DeleteAsync(CancellationToken ct = default)
    {
        var result = await RunSimpleAsync("delete", ct);
        return result.Deleted;
    }

    private ProcessStartInfo BuildPsi(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"\"{AuthScript}\" {args}",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        // 强制 Python 子进程 stdout/stderr 均以 UTF-8 输出，
        // 否则在中文 Windows 上会退化为 GBK，导致 C# 侧按 UTF-8 解码出乱码。
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        return psi;
    }

    private async Task<AuthEvent> RunSimpleAsync(string args, CancellationToken ct)
    {
        var psi = BuildPsi(args);
        using var process = new Process { StartInfo = psi };
        process.Start();
        App.Log($"AuthService: 子进程已启动 {args}, PID={process.Id}");

        var ev = new AuthEvent();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? el = await process.StandardError.ReadLineAsync(ct);
                    if (el == null) break;
                    if (!string.IsNullOrWhiteSpace(el))
                        App.Log($"AuthService: {args} stderr {Trim(el)}");
                }
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
                App.Log($"AuthService: {args} stdout {Trim(line)}");
                try
                {
                    ev = ParseEvent(line);
                }
                catch (JsonException ex)
                {
                    App.Log($"AuthService: JSON 解析失败: {ex.Message}, 行: {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }
        finally
        {
            try { await process.WaitForExitAsync(ct); } catch { }
        }
        return ev;
    }

    private static AuthEvent ParseEvent(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var e = new AuthEvent { Type = root.GetProperty("type").GetString() ?? "" };
        if (root.TryGetProperty("image", out var img)) e.Image = img.GetString();
        if (root.TryGetProperty("url", out var url)) e.Url = url.GetString();
        if (root.TryGetProperty("state", out var st)) e.State = st.GetString();
        if (root.TryGetProperty("message", out var msg)) e.Message = msg.GetString();
        if (root.TryGetProperty("code", out var code)) e.Code = code.GetInt32();
        if (root.TryGetProperty("count", out var count)) e.Count = count.GetInt32();
        if (root.TryGetProperty("deleted", out var del)) e.Deleted = del.GetBoolean();
        if (root.TryGetProperty("refreshed", out var refr)) e.Refreshed = refr.GetBoolean();
        if (root.TryGetProperty("ok", out var ok)) e.Ok = ok.GetBoolean();
        return e;
    }

    private static void KillProcess(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch { }
    }

    private static string Trim(string line) =>
        line.Length > 500 ? line[..500] + "..." : line;

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
