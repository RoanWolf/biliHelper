namespace BiliHelperWpf.Models;

/// <summary>
/// 流式加载事件的类型。
/// </summary>
public enum StreamEventType
{
    Meta,
    Part,
    Complete,
    Error,
}

/// <summary>
/// 流式加载事件，对应 Python 后端 get_subtitles_stream() yield 的每一行 JSON。
/// </summary>
public class StreamEvent
{
    public StreamEventType Type { get; init; }

    // ── Meta 字段 ──
    public string? Title { get; init; }
    public string? BvId { get; init; }
    public int TotalParts { get; init; }

    // ── Part 字段 ──
    public int PartNumber { get; init; }
    public string? PartTitle { get; init; }
    public double? Duration { get; init; }
    public int SubtitleCount { get; init; }
    public string? SubtitleSource { get; init; }
    public string? SubtitleLang { get; init; }
    public List<SubtitleEntry>? Entries { get; set; }

    // ── Complete 字段 ──
    public string? Status { get; init; }
}
