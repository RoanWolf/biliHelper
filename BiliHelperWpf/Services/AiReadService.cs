using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliHelperWpf.Models;

namespace BiliHelperWpf.Services;

/// <summary>
/// AI 整理回调元信息：子进程启动后立即回调（标题、条数等）。
/// </summary>
public sealed record AiReadMeta(
    string BvId,
    string Title,
    string PartTitle,
    int PartNumber,
    int SubtitleCount);

/// <summary>
/// AI 阅读版生成服务。
///
/// 启动 Python 子进程 ai_read.py（单分P 一次 DeepSeek 调用），
/// 逐行读取 stdout 的 JSONL，把结果解析为 Paragraph 列表。
///
/// 子进程契约（详见 BiliHelperCore/AiHelper/ai_read.py）:
///   stdout: {"type":"meta",...} / {"type":"complete","paragraphs":[...]}（一行一个 JSON）
///   stderr: 进度文本 / "[ERROR] ..." 错误信息
///   exit 0 = 成功, exit 1 = 失败
///
/// 回调在后台线程触发，调用方需自行切回 UI 线程（参考 BiliService 用法）。
/// </summary>
public class AiReadService
{
    private static readonly string RepoRoot = FindDirContaining("bilihelperCore")
        ?? AppDomain.CurrentDomain.BaseDirectory;
    private static readonly string CoreDir =
        Path.Combine(RepoRoot, "bilihelperCore");
    private static readonly string PythonScript =
        Path.Combine(CoreDir, "AiHelper", "ai_read.py");
    // 直接调用 .venv 里的 python.exe，绕开 uv run 的环境发现（与 bili_helper.py 调 yt-dlp 一致）
    private static readonly string PythonExe =
        Path.Combine(CoreDir, ".venv", "Scripts", "python.exe");

    /// <summary>
    /// 将模型设置注入子进程环境变量（优先使用传入的 settings，否则用已持久化的）。
    /// 非空才注入：Python 端 AIClient.from_env 只读环境变量，缺失回退默认值；
    /// 空字段不注入，避免空字符串覆盖默认值。
    /// </summary>
    private static void ApplySettings(ProcessStartInfo psi, BiliHelperWpf.Models.AiSettings? settings = null)
    {
        settings ??= AiSettingsStore.Load();
        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
            psi.EnvironmentVariables["DEEPSEEK_API_KEY"] = settings.ApiKey.Trim();
        if (!string.IsNullOrWhiteSpace(settings.BaseUrl))
            psi.EnvironmentVariables["DEEPSEEK_BASE_URL"] = settings.BaseUrl.Trim();
        if (!string.IsNullOrWhiteSpace(settings.Model))
            psi.EnvironmentVariables["DEEPSEEK_MODEL"] = settings.Model.Trim();
    }

    /// <summary>
    /// 为指定 BV ID 的指定分P 生成 AI 阅读版。
    ///
    /// onMeta:    子进程启动后回调元信息（标题、条数等）。
    /// onComplete:AI 整理完成，回调段落列表（调用方负责持久化 read.json）。
    /// onError:   失败回调错误信息（尽量带具体原因）。
    /// progress:  后台进度文本（如 "正在调用 DeepSeek..."）。
    /// ct:        取消时终止子进程（含 uv/python 整棵进程树）并抛出 OperationCanceledException。
    /// </summary>
    public async Task GenerateReadDataAsync(
        string bvId,
        int partNumber,
        Action<AiReadMeta>? onMeta = null,
        Action<List<Paragraph>>? onComplete = null,
        Action<string>? onError = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // ── 定位 raw.json ──────────────────────────────────────
        var rawPath = HistoryService.FindRawJson(bvId);
        if (rawPath == null)
        {
            onError?.Invoke($"未找到历史数据: {bvId}");
            App.Log($"AiReadService: 找不到 raw.json, bvId={bvId}");
            return;
        }

        App.Log($"AiReadService 开始, bvId={bvId}, part={partNumber}, raw={rawPath}");
        progress?.Report("准备启动 AI 整理...");

        // ── 启动子进程 ─────────────────────────────────────────
        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"\"{PythonScript}\" --raw \"{rawPath}\" --part {partNumber}",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        ApplySettings(psi);

        using var process = new Process { StartInfo = psi };
        process.Start();
        App.Log($"AiReadService: Python 进程已启动, PID={process.Id}");

        // ── 异步读取 stderr（进度文本 + 错误信息）──────────────
        var errorLines = new List<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? line = await process.StandardError.ReadLineAsync(ct);
                    if (line == null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    errorLines.Add(line);
                    App.Log($"AiReadService: stderr {TrimProgressLine(line)}");
                    progress?.Report(TrimProgressLine(line));
                }
            }
            catch (OperationCanceledException)
            {
                // 取消由主读取循环统一处理
            }
        }, ct);

        // ── 逐行读取 stdout（JSONL）────────────────────────────
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await process.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                var trimmedOut = line.Length > 500 ? line[..500] + "..." : line;
                App.Log($"AiReadService: stdout {trimmedOut}");
                try
                {
                    ParseLine(line, onMeta, onComplete);
                }
                catch (JsonException ex)
                {
                    App.Log($"AiReadService: JSON 解析失败: {ex.Message}, 行: {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            App.Log("AiReadService: 读取被取消，终止子进程");
            KillProcess(process);
            throw;
        }

        // ── 等待退出 ───────────────────────────────────────────
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var errorMsg = ExtractError(errorLines) ?? "AI 整理失败";
            App.Log($"AiReadService: 子进程退出码 {process.ExitCode}, 错误: {errorMsg}");
            onError?.Invoke(errorMsg);
            return;
        }

        App.Log($"AiReadService: 完成, bvId={bvId}, part={partNumber}, exit=0");
    }

    /// <summary>
    /// 连通性测试：以当前保存的设置（或为空时回退 .env）启动
    /// `ai_read.py --test`，返回是否连通及分类提示信息。
    /// </summary>
    public async Task<(bool ok, string message)> TestConnectivityAsync(
        BiliHelperWpf.Models.AiSettings? settings = null,
        CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"\"{PythonScript}\" --test",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        ApplySettings(psi, settings);

        using var process = new Process { StartInfo = psi };
        process.Start();
        App.Log($"AiReadService: 连通性测试子进程已启动, PID={process.Id}");

        var stderr = new List<string>();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? line = await process.StandardError.ReadLineAsync(ct);
                    if (line == null) break;
                    if (!string.IsNullOrWhiteSpace(line))
                        stderr.Add(line);
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
                App.Log($"AiReadService: test stdout {TrimProgressLine(line)}");
            }
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        // 无论退出码都先尝试解析 stdout 的 JSON：--test 失败时同样会输出
        // {"ok":false,"message":"..."}（exit=1），解析它才能拿到分类错误信息。
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
            }
            catch (JsonException) { }
        }

        var fallback = ExtractError(stderr)
            ?? (process.ExitCode == 0 ? null : $"连通性测试失败（退出码 {process.ExitCode}）")
            ?? "连通性测试失败";
        return (false, fallback);
    }

    // ── 解析 ─────────────────────────────────────────────────

    private static void ParseLine(
        string line,
        Action<AiReadMeta>? onMeta,
        Action<List<Paragraph>>? onComplete)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        string type = root.GetProperty("type").GetString() ?? "";

        switch (type)
        {
            case "meta":
                {
                    var meta = new AiReadMeta(
                        BvId: root.GetProperty("bv_id").GetString() ?? "",
                        Title: root.GetProperty("title").GetString() ?? "",
                        PartTitle: root.GetProperty("part_title").GetString() ?? "",
                        PartNumber: root.GetProperty("part_number").GetInt32(),
                        SubtitleCount: root.GetProperty("subtitle_count").GetInt32());
                    App.Log($"AiReadService: 收到 meta, bvId={meta.BvId}, part={meta.PartNumber}, 条数={meta.SubtitleCount}");
                    onMeta?.Invoke(meta);
                    break;
                }

            case "complete":
                {
                    if (root.TryGetProperty("paragraphs", out var pElem))
                    {
                        var paragraphs = ParseParagraphs(pElem);
                        App.Log($"AiReadService: 收到 complete, 段落数={paragraphs.Count}");
                        onComplete?.Invoke(paragraphs);
                    }
                    break;
                }
        }
    }

    private static List<Paragraph> ParseParagraphs(JsonElement elem)
    {
        var list = new List<Paragraph>();
        if (elem.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in elem.EnumerateArray())
        {
            list.Add(new Paragraph
            {
                Id = item.GetProperty("id").GetInt32(),
                Text = item.GetProperty("text").GetString() ?? "",
                SourceStartIndex = item.GetProperty("source_start_index").GetInt32(),
                SourceEndIndex = item.GetProperty("source_end_index").GetInt32(),
            });
        }
        return list;
    }

    // ── 工具 ─────────────────────────────────────────────────

    private static void KillProcess(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch { /* 进程可能已退出 */ }
    }

    private static string? ExtractError(List<string> errorLines)
    {
        foreach (var raw in errorLines)
        {
            var line = raw.Trim();
            if (line.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase))
            {
                var msg = line["[ERROR]".Length..].Trim();
                if (msg.Length > 0)
                    return msg;
            }
        }
        if (errorLines.Count > 0)
            return string.Join(Environment.NewLine, errorLines.TakeLast(5));
        return null;
    }

    private static string TrimProgressLine(string line)
    {
        if (line.Length > 150)
            line = line[..150] + "...";
        return line.Trim();
    }

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
