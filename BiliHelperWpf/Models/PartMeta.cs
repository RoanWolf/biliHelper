namespace BiliHelperWpf.Models;

/// <summary>
/// index.json 中的分P 索引项：仅元信息，不含字幕 entries。
/// 历史列表与分P 元信息加载只需读 index.json；字幕本体按需从 parts/NNN.json 懒加载。
/// </summary>
public class PartMeta
{
    public int PartNumber { get; init; }
    public string PartTitle { get; init; } = string.Empty;

    /// <summary>时长（秒），可能为 null。</summary>
    public double? Duration { get; init; }

    public int SubtitleCount { get; init; }
    public string? SubtitleSource { get; init; }
    public string? SubtitleLang { get; init; }
}
