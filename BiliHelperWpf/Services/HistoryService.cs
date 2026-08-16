using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using BiliHelperWpf.Models;

namespace BiliHelperWpf.Services;

/// <summary>
/// 管理历史记录的 JSON 文件存储。
///
/// 目录结构（分P 拆分：index + parts，字幕按分P 懒加载）:
///   history/
///   ├── 20260728/
///   │   ├── BV1sC516rEQs/
///   │   │   ├── index.json          ← 轻量：Meta 元信息 + Parts 分P 索引（无 entries）
///   │   │   ├── parts/
///   │   │   │   ├── 001.json        ← 单分P：元信息 + entries 字幕
///   │   │   │   └── 002.json
///   │   │   └── read.json           ← AI 阅读数据（分P 增量，与旧版一致）
///   │   └── ...
///   └── ...
///
/// 日期文件夹（YYYYMMDD）为索引，BV 文件夹内 index+parts 结构。
/// 旧版单文件 raw.json（{meta, data}）仍可读（读端兼容），重新 fetch 后自动升级为拆分格式。
/// </summary>
public static class HistoryService
{
    private static readonly string HistoryDir;

    /// <summary>
    /// 保护 read.json 的"读-合并-写回"原子性。
    /// AI 多个分P 并行整理完成时，防止并发写导致的丢数据。
    /// </summary>
    private static readonly object _readFileLock = new();

    /// <summary>
    /// 无 BOM 的 UTF-8 编码。.NET 的 Encoding.UTF8 静态属性默认带 BOM（EF BB BF），
    /// Python 侧按 utf-8 读取会报错或产生首条数据脏字符，统一用无 BOM 编码写入。
    /// </summary>
    private static readonly System.Text.Encoding Utf8NoBom = new System.Text.UTF8Encoding(false);

    /// <summary>
    /// index.json 的磁盘结构（PascalCase，与全项目存储命名一致）。
    /// </summary>
    internal sealed class IndexFile
    {
        public HistoryItem Meta { get; init; } = new();
        public List<PartMeta> Parts { get; init; } = [];

        /// <summary>B站视频封面 URL（视频级，供飞书文档封面；旧数据无此字段）。</summary>
        public string? CoverUrl { get; init; }
    }

    static HistoryService()
    {
        // 优先放在 WPF 项目根目录（开发时），否则放在 exe 同级（发布后）
        var baseDir = FindProjectRoot() ?? AppDomain.CurrentDomain.BaseDirectory;
        HistoryDir = Path.Combine(baseDir, "history");
        try { Directory.CreateDirectory(HistoryDir); }
        catch { }
    }

    /// <summary>
    /// 向上查找项目根目录（包含 BiliHelperWpf.csproj）。
    /// </summary>
    private static string? FindProjectRoot()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 5; i++)
        {
            if (dir == null) break;
            if (File.Exists(Path.Combine(dir.FullName, "BiliHelperWpf.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// 获取某天文件夹路径。
    /// </summary>
    private static string GetDateDirPath(string dateKey)
    {
        return Path.Combine(HistoryDir, dateKey);
    }

    /// <summary>
    /// 删除所有日期目录下同 BV 的重复目录（保留 keepDir）。
    /// </summary>
    private static void CleanupDuplicateBvDirs(string bvId, string keepDir)
    {
        if (!Directory.Exists(HistoryDir))
            return;

        foreach (var dateDir in Directory.GetDirectories(HistoryDir))
        {
            var dup = Path.Combine(dateDir, bvId);
            if (string.Equals(dup, keepDir, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Directory.Exists(dup))
                continue;
            try
            {
                Directory.Delete(dup, recursive: true);
                App.Log($"清理重复历史目录: {dup}");
            }
            catch (Exception ex)
            {
                App.Log($"清理重复历史目录失败: {ex.Message}");
            }
        }
    }

    private static JsonSerializerOptions SerializerOptions() => new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ─────────────────────────────────────────────────────────────
    // 路径定位
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 在所有日期文件夹中搜索指定 BV 的视频目录，返回目录路径或 null。
    /// 目录存在即视为有记录（无论里面是旧 raw.json 还是新 index.json）。
    /// 同一 BV 可能因重复拉取存在于多个日期目录 —— 固定取最新日期目录
    /// （YYYYMMDD 字符串序即时间序），保证 read.json / parts 定位不分裂。
    /// </summary>
    /// <summary>
    /// 公开入口：定位某 BV 的视频目录（供 FeishuService/MainViewModel 同步用）。
    /// </summary>
    public static string? FindVideoDirectory(string bvId) => FindVideoDir(bvId);

    private static string? FindVideoDir(string bvId)
    {
        if (!Directory.Exists(HistoryDir))
            return null;

        foreach (var dateDir in Directory.GetDirectories(HistoryDir).OrderByDescending(d => d))
        {
            var dirPath = Path.Combine(dateDir, bvId);
            if (Directory.Exists(dirPath))
                return dirPath;
        }
        return null;
    }

    /// <summary>
    /// 旧版单文件 raw.json 路径（仅兼容旧数据时用）；新格式不存在该文件。
    /// </summary>
    public static string? FindRawJson(string bvId)
    {
        var dir = FindVideoDir(bvId);
        if (dir == null) return null;
        var f = Path.Combine(dir, "raw.json");
        return File.Exists(f) ? f : null;
    }

    /// <summary>
    /// 定位某分P 的 parts/NNN.json 完整路径；不存在返回 null。
    /// 供 AiReadService 定位单分P 字幕文件（--part-file 契约）。
    /// </summary>
    public static string? FindPartJson(string bvId, int partNumber)
    {
        var dir = FindVideoDir(bvId);
        if (dir == null) return null;
        var f = Path.Combine(dir, "parts", $"{partNumber:D3}.json");
        return File.Exists(f) ? f : null;
    }

    // ─────────────────────────────────────────────────────────────
    // 查询
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 检查某个 BV ID 是否已有历史记录（扫描所有日期文件夹）。
    /// </summary>
    public static bool Exists(string bvId)
    {
        return FindVideoDir(bvId) != null;
    }

    // ─────────────────────────────────────────────────────────────
    // 写入（fetch 完成后）
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 从流式加载完成的数据创建历史记录：写 index.json（元信息 + 分P 索引）
    /// + parts/NNN.json（每分P 字幕），并删除旧的单文件 raw.json（升级）。
    /// </summary>
    public static void SaveFromVideoInfo(BiliVideoInfo info)
    {
        if (string.IsNullOrEmpty(info.BvId))
            return;

        var now = DateTime.Now;
        var saveDir = Path.Combine(GetDateDirPath(now.ToString("yyyyMMdd")), info.BvId);
        try { Directory.CreateDirectory(saveDir); }
        catch { return; }

        // 清理同一 BV 的历史旧目录：重复拉取会写新日期目录，旧目录会变成僵尸
        // （历史列表重复条目 + read.json/parts 定位歧义），统一清理旧目录。
        CleanupDuplicateBvDirs(info.BvId, saveDir);

        var item = new HistoryItem
        {
            BvId = info.BvId,
            Title = info.Title,
            TotalParts = info.TotalParts,
            TotalSubtitles = info.TotalSubtitleCount,
            Status = info.Status,
            FetchTimeIso = now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };

        var opt = SerializerOptions();
        var partMetas = info.Parts.Select(p => new PartMeta
        {
            PartNumber = p.PartNumber,
            PartTitle = p.PartTitle,
            Duration = p.Duration,
            SubtitleCount = p.SubtitleCount,
            SubtitleSource = p.SubtitleSource,
            SubtitleLang = p.SubtitleLang,
        }).ToList();

        // ── index.json ───────────────────────────────────────
        var index = new IndexFile { Meta = item, Parts = partMetas, CoverUrl = info.CoverUrl };
        try
        {
            File.WriteAllText(
                Path.Combine(saveDir, "index.json"),
                JsonSerializer.Serialize(index, opt),
                Utf8NoBom);
        }
        catch (Exception ex)
        {
            App.Log($"保存 index.json 失败: {ex.Message}");
            return;
        }

        // ── parts/NNN.json ───────────────────────────────────
        var partsDir = Path.Combine(saveDir, "parts");
        try { Directory.CreateDirectory(partsDir); }
        catch (Exception ex)
        {
            App.Log($"创建 parts 目录失败: {ex.Message}");
            return;
        }

        int savedParts = 0;
        foreach (var p in info.Parts)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(partsDir, $"{p.PartNumber:D3}.json"),
                    JsonSerializer.Serialize(p, opt),
                    Utf8NoBom);
                savedParts++;
            }
            catch (Exception ex)
            {
                App.Log($"保存分P {p.PartNumber} 失败: {ex.Message}");
            }
        }

        // ── 删除旧 raw.json（升级为拆分格式）─────────────────
        var oldRaw = Path.Combine(saveDir, "raw.json");
        if (File.Exists(oldRaw))
        {
            try { File.Delete(oldRaw); }
            catch { /* 删不掉不影响，读端仍兼容两者 */ }
        }

        App.Log($"历史记录已保存: {saveDir} (index.json + parts/{savedParts})");
    }

    // ─────────────────────────────────────────────────────────────
    // 读取
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 从本地加载视频（返回分P 元信息，entries 不加载，由调用方懒加载）。
    /// 兼容旧版单文件 raw.json。
    /// </summary>
    public static BiliVideoInfo? LoadVideo(string bvId)
    {
        var dir = FindVideoDir(bvId);
        if (dir == null)
            return null;

        var indexPath = Path.Combine(dir, "index.json");
        if (File.Exists(indexPath))
        {
            try
            {
                var json = File.ReadAllText(indexPath, Utf8NoBom);
                var idx = JsonSerializer.Deserialize<IndexFile>(json);
                if (idx == null || string.IsNullOrEmpty(idx.Meta.BvId))
                    return null;

                var info = new BiliVideoInfo
                {
                    Status = idx.Meta.Status,
                    BvId = idx.Meta.BvId,
                    Title = idx.Meta.Title,
                    TotalParts = idx.Meta.TotalParts,
                    CoverUrl = idx.CoverUrl,
                };

                foreach (var pm in idx.Parts)
                {
                    info.Parts.Add(new PartInfo
                    {
                        PartNumber = pm.PartNumber,
                        PartTitle = pm.PartTitle,
                        Duration = pm.Duration,
                        SubtitleCount = pm.SubtitleCount,
                        SubtitleSource = pm.SubtitleSource,
                        SubtitleLang = pm.SubtitleLang,
                        // 无字幕分P 无需懒加载，直接标记已加载
                        EntriesLoaded = pm.SubtitleCount == 0,
                    });
                }
                return info;
            }
            catch (Exception ex)
            {
                App.Log($"加载 index.json 失败: {ex.Message}");
                return null;
            }
        }

        // 兼容旧格式（raw.json：{meta, data} 或直接 BiliVideoInfo）
        var rawPath = FindRawJson(bvId);
        if (rawPath == null)
            return null;

        try
        {
            var json = File.ReadAllText(rawPath, Utf8NoBom);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataElem))
            {
                var info = JsonSerializer.Deserialize<BiliVideoInfo>(dataElem.GetRawText());
                MarkAllEntriesLoaded(info);
                return info;
            }

            var legacy = JsonSerializer.Deserialize<BiliVideoInfo>(json);
            MarkAllEntriesLoaded(legacy);
            return legacy;
        }
        catch (Exception ex)
        {
            App.Log($"加载历史数据失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 旧格式一次性读全量时，标记所有分P entries 已加载。
    /// </summary>
    private static void MarkAllEntriesLoaded(BiliVideoInfo? info)
    {
        if (info == null) return;
        foreach (var p in info.Parts)
            p.EntriesLoaded = true;
    }

    /// <summary>
    /// 懒加载单个分P 的字幕 entries（读 parts/NNN.json）。
    /// 返回 null 表示加载失败（无该文件或解析错误）。
    /// </summary>
    public static List<SubtitleEntry>? LoadPartEntries(string bvId, int partNumber)
    {
        var partFile = FindPartJson(bvId, partNumber);
        if (partFile == null)
            return null;

        try
        {
            var json = File.ReadAllText(partFile, Utf8NoBom);
            var part = JsonSerializer.Deserialize<PartInfo>(json);
            return part?.Entries?.ToList() ?? [];
        }
        catch (Exception ex)
        {
            App.Log($"加载分P {partNumber} 字幕失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 加载指定 BV ID 的 AI 阅读数据（read.json），与 index/parts 同目录。
    /// 返回 parts 列表（仅包含已整理的分P）；无 read.json 或解析失败返回 null。
    /// </summary>
    public static List<ReadPartData>? LoadReadParts(string bvId)
    {
        var dir = FindVideoDir(bvId);
        if (dir == null)
            return null;

        var readPath = Path.Combine(dir, "read.json");
        if (!File.Exists(readPath))
            return null;

        try
        {
            var json = File.ReadAllText(readPath, Utf8NoBom);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("parts", out var partsElem)
                && partsElem.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<ReadPartData>>(partsElem.GetRawText());
            }
            return null;
        }
        catch (Exception ex)
        {
            App.Log($"加载阅读数据失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 增量保存单个分P 的 AI 阅读数据到 read.json（与 index/parts 同目录）。
    /// 已存在相同分P 时替换，其余分P 保留。
    /// </summary>
    public static void SaveReadPart(string bvId, ReadPartData part)
    {
        if (part == null) return;

        var dir = FindVideoDir(bvId);
        if (dir == null)
            return;

        var readPath = Path.Combine(dir, "read.json");

        // 加锁保证"读-合并-写回"原子性：多个分P 并发完成时排队写，避免覆盖丢失
        lock (_readFileLock)
        {
            var parts = LoadReadParts(bvId) ?? new List<ReadPartData>();
            parts.RemoveAll(p => p.PartNumber == part.PartNumber);
            parts.Add(part);
            parts.Sort((a, b) => a.PartNumber.CompareTo(b.PartNumber));

            var dataToSave = new { parts };
            try
            {
                File.WriteAllText(
                    readPath,
                    JsonSerializer.Serialize(dataToSave, SerializerOptions()),
                    Utf8NoBom);
                App.Log($"阅读数据已保存: {readPath}");
            }
            catch (Exception ex)
            {
                App.Log($"保存阅读数据失败: {ex.Message}");
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    // 历史列表
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// 加载所有历史记录，按日期分组（倒序）、组内按时间倒序。
    /// 新格式只读 index.json（轻量），旧格式回退读 raw.json。
    /// </summary>
    public static List<HistoryGroup> LoadGroups()
    {
        var groups = new List<HistoryGroup>();

        if (!Directory.Exists(HistoryDir))
            return groups;

        var dateDirs = Directory.GetDirectories(HistoryDir)
            .Select(d => new DirectoryInfo(d))
            .Where(d => Regex.IsMatch(d.Name, @"^\d{8}$"))  // 只认 YYYYMMDD 格式
            .OrderByDescending(d => d.Name)
            .ToList();

        foreach (var dateDir in dateDirs)
        {
            var group = new HistoryGroup
            {
                DateKey = dateDir.Name,
                GroupTitle = FormatDateGroup(dateDir.Name),
            };

            var bvDirs = Directory.GetDirectories(dateDir.FullName)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastWriteTime)
                .ToList();

            foreach (var bvDir in bvDirs)
            {
                try
                {
                    var indexPath = Path.Combine(bvDir.FullName, "index.json");
                    if (File.Exists(indexPath))
                    {
                        var json = File.ReadAllText(indexPath, Utf8NoBom);
                        var idx = JsonSerializer.Deserialize<IndexFile>(json);
                        if (idx?.Meta != null && !string.IsNullOrEmpty(idx.Meta.BvId))
                            group.Items.Add(idx.Meta);
                        continue;
                    }

                    // 旧格式 raw.json
                    var rawFile = Path.Combine(bvDir.FullName, "raw.json");
                    if (!File.Exists(rawFile))
                        continue;

                    var rawJson = File.ReadAllText(rawFile, Utf8NoBom);
                    using var doc = JsonDocument.Parse(rawJson);

                    if (doc.RootElement.TryGetProperty("meta", out var metaElem))
                    {
                        var item = JsonSerializer.Deserialize<HistoryItem>(metaElem.GetRawText());
                        if (item != null)
                            group.Items.Add(item);
                    }
                    else if (doc.RootElement.TryGetProperty("data", out var dataElem))
                    {
                        var info = JsonSerializer.Deserialize<BiliVideoInfo>(dataElem.GetRawText());
                        if (info != null)
                        {
                            group.Items.Add(new HistoryItem
                            {
                                BvId = info.BvId,
                                Title = info.Title,
                                TotalParts = info.TotalParts,
                                TotalSubtitles = info.TotalSubtitleCount,
                                Status = info.Status,
                                FetchTimeIso = bvDir.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ss"),
                            });
                        }
                    }
                }
                catch
                {
                    // 文件损坏跳过
                }
            }

            if (group.Items.Count > 0)
                groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// 删除一条历史记录（递归删除 BV 目录）。
    /// </summary>
    public static void Delete(string bvId)
    {
        var dir = FindVideoDir(bvId);
        if (dir == null) return;

        try
        {
            Directory.Delete(dir, recursive: true);

            // 如果日期文件夹为空，一起删除
            var dateDir = Path.GetDirectoryName(dir);
            if (dateDir != null && Directory.Exists(dateDir) && !Directory.EnumerateFileSystemEntries(dateDir).Any())
            {
                Directory.Delete(dateDir);
            }

            App.Log($"历史记录已删除: {bvId}");
        }
        catch (Exception ex)
        {
            App.Log($"删除历史数据失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将 "20260728" 格式化为 "2026年07月28日"。
    /// </summary>
    private static string FormatDateGroup(string dateKey)
    {
        if (dateKey.Length != 8)
            return dateKey;

        try
        {
            return $"{dateKey[..4]}年{dateKey[4..6]}月{dateKey[6..8]}日";
        }
        catch
        {
            return dateKey;
        }
    }
}
