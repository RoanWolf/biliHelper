using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace BiliHelperWpf.Models;

/// <summary>
/// 单个分P的信息。
/// </summary>
public class PartInfo
{
    public int PartNumber { get; init; }
    public string PartTitle { get; init; } = string.Empty;
    public double? Duration { get; init; }

    /// <summary>
    /// 格式化后的时长，如 "12:34"。
    /// </summary>
    [JsonIgnore]
    public string DurationFormatted => Duration.HasValue
        ? FormatDuration(Duration.Value)
        : "--:--";

    public int SubtitleCount { get; set; }
    public string? SubtitleSource { get; set; }
    public string? SubtitleLang { get; set; }
    public ObservableCollection<SubtitleEntry> Entries { get; set; } = [];

    /// <summary>
    /// 运行时标志：entries 是否已加载（历史加载时懒加载用，不参与序列化）。
    /// fetch 流式路径逐 P 到达即置 true；无字幕分P（SubtitleCount==0）恒为 true。
    /// </summary>
    [JsonIgnore]
    public bool EntriesLoaded { get; set; }

    /// <summary>
    /// 字幕来源的友好描述。
    /// </summary>
    [JsonIgnore]
    public string SubtitleSourceDisplay => SubtitleSource switch
    {
        "ai" => "AI 字幕",
        "manual" => "人工字幕",
        "danmaku" => "弹幕",
        _ => SubtitleSource ?? "无字幕"
    };

    /// <summary>
    /// 是否有字幕。
    /// </summary>
    [JsonIgnore]
    public bool HasSubtitles => SubtitleCount > 0;

    /// <summary>
    /// 显示在列表中的标题，如 "P1 · 绪论 (12:34)"。
    /// </summary>
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(PartTitle)
        ? $"P{PartNumber}"
        : $"P{PartNumber} {PartTitle}";

    private static string FormatDuration(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
