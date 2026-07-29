namespace BiliHelperWpf.Models;

/// <summary>
/// 历史记录条目，对应 index.json 中的一条记录。
/// 同时作为历史记录抽屉列表的展示模型。
/// </summary>
public class HistoryItem
{
    public string BvId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string FetchTime { get; init; } = string.Empty;
    public int TotalParts { get; init; }
    public int TotalSubtitles { get; init; }
    public string Status { get; init; } = string.Empty;

    // ── 展示用计算属性 ──

    /// <summary>
    /// 标题截断（太长时）
    /// </summary>
    public string DisplayTitle => Title.Length > 50 ? Title[..47] + "..." : Title;

    /// <summary>
    /// 取时间字符串中的日期部分 "2026-07-28"
    /// </summary>
    public string DisplayDate => FetchTime.Length >= 10 ? FetchTime[..10] : FetchTime;

    /// <summary>
    /// 状态灯颜色标识
    /// </summary>
    public string StatusDisplay => Status switch
    {
        "ok" => "全部字幕",
        "partial" => "部分字幕",
        "empty" => "无字幕",
        _ => Status
    };

    /// <summary>
    /// 字幕数/分P 摘要
    /// </summary>
    public string Summary => $"{TotalSubtitles} 条字幕 · {TotalParts}P";
}
