using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

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
    /// B站视频封面 URL（视频级，所有分P 相同；供飞书文档封面）。
    /// 拉取时从 part 事件的 cover_url 取第一个非空值；随 index.json 的 CoverUrl 持久化。
    /// </summary>
    public string? CoverUrl { get; set; }

    /// <summary>
    /// B站 UP 主名（视频级；供飞书卡片消息作者展示）。
    /// 拉取时从 part 事件的 uploader 取第一个非空值；随 index.json 的 Uploader 持久化。
    /// </summary>
    public string? Uploader { get; set; }

    /// <summary>
    /// 状态的中文描述。
    /// </summary>
    [JsonIgnore]
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
    [JsonIgnore]
    public int TotalSubtitleCount => Parts.Sum(p => p.SubtitleCount);

    /// <summary>
    /// 是否有任何字幕数据。
    /// </summary>
    [JsonIgnore]
    public bool HasAnySubtitle => Parts.Any(p => p.HasSubtitles);
}
