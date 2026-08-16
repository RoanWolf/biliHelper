using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BiliHelperWpf.Models;

namespace BiliHelperWpf.Services;

/// <summary>
/// 流式调用 Python 后端，逐行读取 stdout，实时解析每个分P 的字幕。
/// </summary>
public class BiliService
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string PythonScript = Path.Combine(ProjectRoot, "bilihelperCore", "main.py");
    private static readonly string CookieFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BiliHelper", "cookies.txt");
    private static readonly string PythonExe = Path.Combine(ProjectRoot, "bilihelperCore", ".venv", "Scripts", "python.exe");

    /// <summary>
    /// 流式获取字幕。
    /// onMeta: 收到视频元信息时回调
    /// onPart: 每个分P 加载完成后回调
    /// onComplete: 全部加载完成后回调
    /// onError: 发生错误时回调
    /// progress: 进度回调 (文本, 百分比)
    /// </summary>
    public async Task FetchStreamAsync(
        string url,
        Action<StreamEvent> onMeta,
        Action<StreamEvent> onPart,
        Action<StreamEvent> onComplete,
        Action<string> onError,
        IProgress<(string text, double percent)>? progress = null,
        CancellationToken ct = default)
    {
        App.Log($"FetchStreamAsync 开始, URL: {url}");
        App.Log($"项目根目录: {ProjectRoot}");

        progress?.Report(("准备启动...", 0));

        var psi = new ProcessStartInfo
        {
            FileName = PythonExe,
            Arguments = $"\"{PythonScript}\" \"{url}\" -c \"{CookieFile}\" --stream",
            WorkingDirectory = ProjectRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = psi };
        process.Start();

        App.Log($"Python 进程已启动, PID={process.Id}");

        // ── 异步读取 stderr（进度信息） ──
        var errorBuilder = new StringBuilder();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    string? line = await process.StandardError.ReadLineAsync(ct);
                    if (line == null) break;
                    errorBuilder.AppendLine(line);

                    double percent = TryExtractPercent(line);
                    string display = TrimProgressLine(line);
                    progress?.Report((display, percent >= 0 ? 5.0 + percent * 0.85 : -1));
                }
            }
            catch (OperationCanceledException) { }
        }, ct);

        // ── 逐行读取 stdout（流式 JSON） ──
        int partsLoaded = 0;
        int totalParts = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? line = await process.StandardOutput.ReadLineAsync(ct);
                if (line == null) break;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    StreamEvent ev = ParseLine(line);

                    switch (ev.Type)
                    {
                        case StreamEventType.Meta:
                            totalParts = ev.TotalParts;
                            onMeta?.Invoke(ev);
                            progress?.Report(("已获取视频信息", 5));
                            break;

                        case StreamEventType.Part:
                            partsLoaded++;
                            onPart?.Invoke(ev);
                            if (totalParts > 0)
                            {
                                double pct = 5.0 + (double)partsLoaded / totalParts * 85.0;
                                progress?.Report(($"已加载 P{ev.PartNumber}/{totalParts}", pct));
                            }
                            break;

                        case StreamEventType.Complete:
                            onComplete?.Invoke(ev);
                            progress?.Report(("完成", 100));
                            break;
                    }
                }
                catch (JsonException ex)
                {
                    App.Log($"JSON 解析失败: {ex.Message}, 行内容: {line}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            App.Log("流式读取被取消，终止子进程树");
            KillProcess(process);
            throw;
        }

        // ── 等待进程退出 ──
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillProcess(process);
            throw;
        }

        string stderr = errorBuilder.ToString().Trim();

        if (process.ExitCode != 0 && !string.IsNullOrEmpty(stderr))
        {
            // 如果已经有部分数据加载，只是报个错但不抛异常
            string msg = stderr.Length > 200 ? stderr[..200] + "..." : stderr;
            onError?.Invoke(msg);
        }

        App.Log($"流式加载完成: 共 {partsLoaded}/{totalParts} 分P");
    }

    /// <summary>
    /// 解析一行 JSON 为 StreamEvent。
    /// </summary>
    private static StreamEvent ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        string type = root.GetProperty("type").GetString() ?? "";

        return type switch
        {
            "meta" => new StreamEvent
            {
                Type = StreamEventType.Meta,
                Title = root.GetProperty("title").GetString(),
                BvId = root.GetProperty("bv_id").GetString(),
                TotalParts = root.GetProperty("total_parts").GetInt32(),
            },

            "part" => ParsePartEvent(root),

            "complete" => new StreamEvent
            {
                Type = StreamEventType.Complete,
                Status = root.GetProperty("status").GetString(),
            },

            _ => throw new JsonException($"未知的事件类型: {type}")
        };
    }

    private static StreamEvent ParsePartEvent(JsonElement root)
    {
        var ev = new StreamEvent
        {
            Type = StreamEventType.Part,
            PartNumber = root.GetProperty("part_number").GetInt32(),
            PartTitle = root.GetProperty("part_title").GetString() ?? "",
            Duration = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
                ? dur.GetDouble()
                : null,
            SubtitleCount = root.TryGetProperty("subtitle_count", out var sc) ? sc.GetInt32() : 0,
            SubtitleSource = root.TryGetProperty("subtitle_source", out var ss) ? ss.GetString() : null,
            SubtitleLang = root.TryGetProperty("subtitle_lang", out var sl) ? sl.GetString() : null,
        };

        if (root.TryGetProperty("entries", out var entriesElem) && entriesElem.ValueKind == JsonValueKind.Array)
        {
            var list = new List<SubtitleEntry>();
            foreach (var entryElem in entriesElem.EnumerateArray())
            {
                list.Add(new SubtitleEntry
                {
                    Index = entryElem.GetProperty("index").GetInt32(),
                    StartTime = entryElem.GetProperty("start_time").GetDouble(),
                    EndTime = entryElem.GetProperty("end_time").GetDouble(),
                    Text = entryElem.GetProperty("text").GetString() ?? "",
                });
            }
            ev.Entries = list;
        }

        return ev;
    }

    private static double TryExtractPercent(string line)
    {
        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+\.?\d*)\s*%");
        if (match.Success && double.TryParse(match.Groups[1].Value, out double pct))
            return pct;
        return -1;
    }

    private static string TrimProgressLine(string line)
    {
        if (line.Length > 150)
            line = line[..150] + "...";
        return line.Trim();
    }

    private static string FindProjectRoot()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 5; i++)
        {
            if (dir == null) break;
            if (Directory.Exists(Path.Combine(dir.FullName, "bilihelperCore")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return baseDir;
    }
    private static void KillProcess(Process process)
    {
        if (process.HasExited) return;
        try { process.Kill(entireProcessTree: true); }
        catch { /* 进程可能已退出 */ }
    }
}
