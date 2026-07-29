using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using BiliHelperWpf.Models;

namespace BiliHelperWpf.Services;

/// <summary>
/// 管理历史记录的 JSON 文件存储。
/// 目录结构:
///   BiliHelperWpf/history/
///   ├── index.json          ← 轻量索引 (HistoryItem 列表)
///   └── BVxxxxxxxx.json    ← 完整视频数据 (BiliVideoInfo)
/// </summary>
public static class HistoryService
{
    private static readonly string HistoryDir;
    private static readonly string IndexPath;

    static HistoryService()
    {
        // history 目录放在 WPF 项目根目录同级
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        HistoryDir = Path.Combine(baseDir, "history");
        IndexPath = Path.Combine(HistoryDir, "index.json");

        // 确保目录存在
        try { Directory.CreateDirectory(HistoryDir); }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 检查某个 BV ID 是否已有历史记录。
    /// </summary>
    public static bool Exists(string bvId)
    {
        var path = GetDataPath(bvId);
        return File.Exists(path);
    }

    /// <summary>
    /// 获取完整的视频数据文件路径。
    /// </summary>
    private static string GetDataPath(string bvId) =>
        Path.Combine(HistoryDir, $"{bvId}.json");

    /// <summary>
    /// 加载所有历史记录索引。
    /// </summary>
    public static List<HistoryItem> LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath))
                return [];

            var json = File.ReadAllText(IndexPath, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<List<HistoryItem>>(json) ?? [];
        }
        catch
        {
            // 索引损坏时返回空列表
            return [];
        }
    }

    /// <summary>
    /// 追加一条历史记录到索引。
    /// </summary>
    private static void AppendIndex(HistoryItem item)
    {
        try
        {
            var items = LoadIndex();

            // 去重：如果已存在同 BV ID，移除旧的
            items.RemoveAll(i => i.BvId == item.BvId);

            // 新记录插在最前面
            items.Insert(0, item);

            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(IndexPath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            App.Log($"写入历史索引失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从流式加载完成的数据创建历史记录，保存到磁盘。
    /// </summary>
    public static void SaveFromVideoInfo(BiliVideoInfo info)
    {
        if (string.IsNullOrEmpty(info.BvId))
            return;

        // 1. 保存完整数据
        var dataPath = GetDataPath(info.BvId);
        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var json = JsonSerializer.Serialize(info, options);
            File.WriteAllText(dataPath, json, System.Text.Encoding.UTF8);
        }
        catch (Exception ex)
        {
            App.Log($"保存历史数据失败: {ex.Message}");
            return;
        }

        // 2. 更新索引
        var item = new HistoryItem
        {
            BvId = info.BvId,
            Title = info.Title,
            FetchTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            TotalParts = info.TotalParts,
            TotalSubtitles = info.TotalSubtitleCount,
            Status = info.Status,
        };
        AppendIndex(item);

        App.Log($"历史记录已保存: {info.BvId} - {info.Title}");
    }

    /// <summary>
    /// 从本地文件加载完整的 BiliVideoInfo。
    /// </summary>
    public static BiliVideoInfo? LoadVideo(string bvId)
    {
        var path = GetDataPath(bvId);
        if (!File.Exists(path))
            return null;

        try
        {
            var json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            return JsonSerializer.Deserialize<BiliVideoInfo>(json);
        }
        catch (Exception ex)
        {
            App.Log($"加载历史数据失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 删除一条历史记录（完整数据 + 索引）。
    /// </summary>
    public static void Delete(string bvId)
    {
        // 删除数据文件
        var dataPath = GetDataPath(bvId);
        try { if (File.Exists(dataPath)) File.Delete(dataPath); }
        catch { }

        // 从索引中移除
        try
        {
            var items = LoadIndex();
            items.RemoveAll(i => i.BvId == bvId);
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
            File.WriteAllText(IndexPath, json, System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
