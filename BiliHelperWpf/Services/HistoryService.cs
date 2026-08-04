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
/// 目录结构:
///   history/
///   ├── 20260728/
///   │   ├── BV1sC516rEQs/
///   │   │   └── raw.json          ← 原始字幕
///   │   └── BV116w5zuEbo/
///   │       └── raw.json
///   ├── 20260727/
///   │   └── BVxxxxxxx/
///   │       └── raw.json
///   └── ...
///
/// 日期文件夹（YYYYMMDD）为索引，BV 文件夹内可扩展多种字幕文件。
/// 不需要单独的 index.json，文件夹本身就是索引。
/// </summary>
public static class HistoryService
{
    private static readonly string HistoryDir;

    /// <summary>
    /// 保护 read.json 的"读-合并-写回"原子性。
    /// AI 多个分P 并行整理完成时，防止并发写导致的丢数据。
    /// </summary>
    private static readonly object _readFileLock = new();

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
    /// 获取某条记录中 raw 字幕的完整文件路径。
    /// 格式: history/YYYYMMDD/BVxxx/raw.json
    /// </summary>
    private static string GetDataPath(string dateKey, string bvId)
    {
        var dir = Path.Combine(HistoryDir, dateKey, bvId);
        return Path.Combine(dir, "raw.json");
    }

    /// <summary>
    /// 获取某天文件夹路径。
    /// </summary>
    private static string GetDateDirPath(string dateKey)
    {
        return Path.Combine(HistoryDir, dateKey);
    }

    /// <summary>
    /// 检查某个 BV ID 是否已有历史记录（扫描所有日期文件夹）。
    /// </summary>
    public static bool Exists(string bvId)
    {
        return FindByBvId(bvId) != null;
    }

    /// <summary>
    /// 在所有日期文件夹中搜索指定 BV ID 的文件路径。
    /// </summary>
    private static string? FindByBvId(string bvId)
    {
        if (!Directory.Exists(HistoryDir))
            return null;

        foreach (var dateDir in Directory.GetDirectories(HistoryDir))
        {
            var dirPath = Path.Combine(dateDir, bvId);
            var filePath = Path.Combine(dirPath, "raw.json");
            if (File.Exists(filePath))
                return filePath;
        }
        return null;
    }

    /// <summary>
    /// 从流式加载完成的数据创建历史记录，保存到磁盘。
    /// </summary>
    public static void SaveFromVideoInfo(BiliVideoInfo info)
    {
        if (string.IsNullOrEmpty(info.BvId))
            return;

        var now = DateTime.Now;
        var dateKey = now.ToString("yyyyMMdd");
        var dir = GetDateDirPath(dateKey);

        var saveDir = Path.Combine(dir, info.BvId);
        try { Directory.CreateDirectory(saveDir); }
        catch { return; }

        // 构建设 HistoryItem 元数据
        var item = new HistoryItem
        {
            BvId = info.BvId,
            Title = info.Title,
            TotalParts = info.TotalParts,
            TotalSubtitles = info.TotalSubtitleCount,
            Status = info.Status,
            FetchTimeIso = now.ToString("yyyy-MM-ddTHH:mm:ss"),
        };

        // 保存完整视频数据（嵌入元信息，方便加载和展示）
        var dataToSave = new
        {
            meta = item,
            data = info
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var json = JsonSerializer.Serialize(dataToSave, options);
        var filePath = Path.Combine(saveDir, "raw.json");

        try
        {
            File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
            App.Log($"历史记录已保存: {filePath}");
        }
        catch (Exception ex)
        {
            App.Log($"保存历史数据失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从本地文件加载完整的 BiliVideoInfo。
    /// </summary>
    public static BiliVideoInfo? LoadVideo(string bvId)
    {
        var filePath = FindByBvId(bvId);
        if (filePath == null)
            return null;

        try
        {
            var json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);

            // 尝试解析新格式（有 meta 包裹）
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataElem))
            {
                return JsonSerializer.Deserialize<BiliVideoInfo>(dataElem.GetRawText());
            }

            // 兼容旧格式（直接就是 BiliVideoInfo）
            return JsonSerializer.Deserialize<BiliVideoInfo>(json);
        }
        catch (Exception ex)
        {
            App.Log($"加载历史数据失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 查找指定 BV ID 的 raw.json 完整路径（扫描所有日期文件夹）。
    /// 目录结构: history/YYYYMMDD/BVxxx/raw.json；不存在返回 null。
    /// </summary>
    public static string? FindRawJson(string bvId)
    {
        return FindByBvId(bvId);
    }

    /// <summary>
    /// 加载指定 BV ID 的 AI 阅读数据（read.json）。
    /// 返回 parts 列表（仅包含已整理的分P）；无 read.json 或解析失败返回 null。
    /// </summary>
    public static List<ReadPartData>? LoadReadParts(string bvId)
    {
        var rawPath = FindByBvId(bvId);
        if (rawPath == null)
            return null;

        var readPath = Path.Combine(Path.GetDirectoryName(rawPath)!, "read.json");
        if (!File.Exists(readPath))
            return null;

        try
        {
            var json = File.ReadAllText(readPath, System.Text.Encoding.UTF8);
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
    /// 增量保存单个分P 的 AI 阅读数据到 read.json。
    /// 与 raw.json 同目录（history/YYYYMMDD/BVxxx/read.json）。
    /// 已存在相同分P 时替换，其余分P 保留。
    /// </summary>
    public static void SaveReadPart(string bvId, ReadPartData part)
    {
        var rawPath = FindByBvId(bvId);
        if (rawPath == null || part == null)
            return;

        var dir = Path.GetDirectoryName(rawPath);
        if (string.IsNullOrEmpty(dir))
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
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            try
            {
                File.WriteAllText(
                    readPath,
                    JsonSerializer.Serialize(dataToSave, options),
                    System.Text.Encoding.UTF8);
                App.Log($"阅读数据已保存: {readPath}");
            }
            catch (Exception ex)
            {
                App.Log($"保存阅读数据失败: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 加载所有历史记录，按日期分组（倒序）、组内按时间倒序。
    /// </summary>
    public static List<HistoryGroup> LoadGroups()
    {
        var groups = new List<HistoryGroup>();

        if (!Directory.Exists(HistoryDir))
            return groups;

        // 获取所有日期文件夹，按名称倒序（最新的日期在前）
        var dateDirs = Directory.GetDirectories(HistoryDir)
            .Select(d => new DirectoryInfo(d))
            .Where(d => Regex.IsMatch(d.Name, @"^\d{8}$"))  // 只认 YYYYMMDD 格式
            .OrderByDescending(d => d.Name)
            .ToList();

        foreach (var dateDir in dateDirs)
        {
            var dateKey = dateDir.Name;
            var group = new HistoryGroup
            {
                DateKey = dateKey,
                GroupTitle = FormatDateGroup(dateKey),
            };

            // 获取该日期下所有 BV 子目录，按子目录修改时间倒序
            var bvDirs = Directory.GetDirectories(dateDir.FullName)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastWriteTime)
                .ToList();

            foreach (var bvDir in bvDirs)
            {
                var rawFile = Path.Combine(bvDir.FullName, "raw.json");
                if (!File.Exists(rawFile))
                    continue;

                try
                {
                    var json = File.ReadAllText(rawFile, System.Text.Encoding.UTF8);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("meta", out var metaElem))
                    {
                        var item = JsonSerializer.Deserialize<HistoryItem>(metaElem.GetRawText());
                        if (item != null)
                            group.Items.Add(item);
                    }
                    else
                    {
                        // 兼容旧格式（直接是 BiliVideoInfo，嵌套在 data 字段里）
                        if (doc.RootElement.TryGetProperty("data", out var dataElem))
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
        var filePath = FindByBvId(bvId);
        if (filePath == null) return;

        try
        {
            var bvDir = Path.GetDirectoryName(filePath);
            if (bvDir != null && Directory.Exists(bvDir))
            {
                Directory.Delete(bvDir, recursive: true);
            }

            // 如果日期文件夹为空，一起删除
            var dateDir = Path.GetDirectoryName(bvDir);
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
