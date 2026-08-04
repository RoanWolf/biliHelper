using System.Text.Json.Serialization;

namespace BiliHelperWpf.Models;

/// <summary>
/// AI 整理后的段落，对应 DeepSeek 返回的 paragraphs[] 中的一条。
/// </summary>
public class Paragraph
{
    /// <summary>
    /// 段落序号（从 1 开始）。
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; init; }

    /// <summary>
    /// 整理后的段落文本（已修正错别字、添加标点和分段）。
    /// </summary>
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// 对应 raw 字幕条目的起始索引（含，从 1 开始）。
    /// </summary>
    [JsonPropertyName("source_start_index")]
    public int SourceStartIndex { get; init; }

    /// <summary>
    /// 对应 raw 字幕条目的结束索引（含）。
    /// </summary>
    [JsonPropertyName("source_end_index")]
    public int SourceEndIndex { get; init; }
}
