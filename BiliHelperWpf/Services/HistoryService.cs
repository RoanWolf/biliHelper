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
///   │   ├── BV1sC516rEQs.json
///   │   └── BV116w5zuEbo.json
///   ├── 20260727/
///   │   └── BVxxxxxxx.json
///   └── ...
///
/// 文件夹名即日期键（YYYYMMDD），文件名为 BV ID。
/// 不需要单独的 index.json，文件夹本身就是索引。
/// </summary>
public static class HistoryService
{
    private static readonly string HistoryDir;

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
    /// 获取某条记录的完整文件路径。
    /// 格式: history/YYYYMMDD/BVxxx.json
    /// </summary>
    private static string GetDataPath(string dateKey, string bvId)
    {
        var dir = Path.Combine(HistoryDir, dateKey);
        return Path.Combine(dir, $"{bvId}.json");
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
            var filePath = Path.Combine(dateDir, $"{bvId}.json");
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

        try { Directory.CreateDirectory(dir); }
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
        var filePath = GetDataPath(dateKey, info.BvId);

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

            // 获取该日期下的所有 JSON 文件，按文件修改时间倒序
            var files = dateDir.GetFiles("*.json")
                .OrderByDescending(f => f.LastWriteTime)
                .ToList();

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file.FullName, System.Text.Encoding.UTF8);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("meta", out var metaElem))
                    {
                        // 新格式：有 meta 字段
                        var item = JsonSerializer.Deserialize<HistoryItem>(metaElem.GetRawText());
                        if (item != null)
                            group.Items.Add(item);
                    }
                    else
                    {
                        // 旧格式：直接是 BiliVideoInfo
                        var info = JsonSerializer.Deserialize<BiliVideoInfo>(json);
                        if (info != null)
                        {
                            group.Items.Add(new HistoryItem
                            {
                                BvId = info.BvId,
                                Title = info.Title,
                                TotalParts = info.TotalParts,
                                TotalSubtitles = info.TotalSubtitleCount,
                                Status = info.Status,
                                FetchTimeIso = file.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:ss"),
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
    /// 删除一条历史记录。
    /// </summary>
    public static void Delete(string bvId)
    {
        var filePath = FindByBvId(bvId);
        if (filePath == null) return;

        try
        {
            File.Delete(filePath);

            // 如果日期文件夹为空，一起删除
            var dir = Path.GetDirectoryName(filePath);
            if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
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
