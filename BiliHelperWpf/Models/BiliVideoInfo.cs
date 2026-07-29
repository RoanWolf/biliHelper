using System.Collections.ObjectModel;

namespace BiliHelperWpf.Models;

/// <summary>
/// 顶层视频信息，对应 Python 后端 get_subtitles() 返回的 JSON 结构。
/// </summary>
public class BiliVideoInfo
{
    public string Status { get; set; } = string.Empty;   // "ok" | "partial" | "empty"
    public string BvId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int TotalParts { get; set; }
    public ObservableCollection<PartInfo> Parts { get; init; } = [];

    /// <summary>
    /// 状态的中文描述。
    /// </summary>
    public string StatusDisplay => Status switch
    {
        "ok" => "全部有字幕 ✅",
        "partial" => "部分有字幕 ⚠️",
        "empty" => "无字幕 ❌",
        _ => Status
    };

    /// <summary>
    /// 总字幕数。
    /// </summary>
    public int TotalSubtitleCount => Parts.Sum(p => p.SubtitleCount);

    /// <summary>
    /// 是否有任何字幕数据。
    /// </summary>
    public bool HasAnySubtitle => Parts.Any(p => p.HasSubtitles);
}
