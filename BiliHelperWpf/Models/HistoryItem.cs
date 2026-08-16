using System;
using System.Text.Json.Serialization;

namespace BiliHelperWpf.Models;

/// <summary>
/// 历史记录条目模型。
/// 对应 history/YYYYMMDD/BVxxx.json 中的一条记录。
/// 同时作为历史抽屉的展示模型，支持按日期分组。
/// </summary>
public class HistoryItem
{
    /// <summary>BV 号，唯一标识。</summary>
    public string BvId { get; init; } = string.Empty;

    /// <summary>视频标题。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>分P 数量。</summary>
    public int TotalParts { get; init; }

    /// <summary>总字幕数。</summary>
    public int TotalSubtitles { get; init; }

    /// <summary>状态。</summary>
    public string Status { get; init; } = string.Empty;

    // ── 时间相关 ────────────────────────────────────────────

    /// <summary>
    /// 完整的拉取时间（ISO 格式，如 "2026-07-28T14:23:00"）。
    /// 序列化到 JSON 中，避免依赖文件系统时间。
    /// </summary>
    public string FetchTimeIso { get; init; } = string.Empty;

    /// <summary>
    /// 日期分组键，格式 "YYYYMMDD"。
    /// 用于文件夹命名和 UI 分组。
    /// </summary>
    [JsonIgnore]
    public string DateKey => FetchTimeIso.Length >= 10
        ? FetchTimeIso[..10].Replace("-", "")
        : "unknown";

    // ── 展示用计算属性 ────────────────────────────────────

    /// <summary>标题截断（过长时）。</summary>
    [JsonIgnore]
    public string DisplayTitle => Title.Length > 50 ? Title[..47] + "..." : Title;

    /// <summary>
    /// 日期分组标题，如 "2026年07月28日"。
    /// </summary>
    [JsonIgnore]
    public string DateGroup
    {
        get
        {
            if (FetchTimeIso.Length < 10) return "未知日期";
            var parts = FetchTimeIso[..10].Split('-');
            if (parts.Length != 3) return "未知日期";
            return $"{parts[0]}年{parts[1]}月{parts[2]}日";
        }
    }

    /// <summary>
    /// 时间，如 "14:23"。
    /// </summary>
    [JsonIgnore]
    public string TimeStr
    {
        get
        {
            if (FetchTimeIso.Length < 16) return "--:--";
            return FetchTimeIso[11..16];
        }
    }

    /// <summary>状态灯中文描述。</summary>
    [JsonIgnore]
    public string StatusDisplay => Status switch
    {
        "ok" => "有字幕",
        "partial" => "部分字幕",
        "empty" => "无字幕",
        _ => Status
    };

    /// <summary>摘要，如 "29P · 87条字幕"。</summary>
    [JsonIgnore]
    public string Summary
    {
        get
        {
            var parts = $"{TotalParts}P";
            var subs = TotalSubtitles > 0
                ? $"{TotalSubtitles}条字幕"
                : "无字幕";
            return $"{parts} · {subs} · {StatusDisplay}";
        }
    }
}
