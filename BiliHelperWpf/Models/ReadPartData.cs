using System.Text.Json.Serialization;

namespace BiliHelperWpf.Models;

/// <summary>
/// 单个分P 的 AI 整理后阅读数据。
/// 对应 read.json 中 parts[] 数组的每一项。
/// </summary>
public class ReadPartData
{
    /// <summary>分P 号，对应 parts/NNN.json 的 PartNumber。</summary>
    [JsonPropertyName("part_number")]
    public int PartNumber { get; init; }

    /// <summary>该分P 的字幕条数（整理时的原始条数）。</summary>
    [JsonPropertyName("subtitle_count")]
    public int SubtitleCount { get; init; }

    /// <summary>
    /// AI 整理出的段落列表。
    /// </summary>
    [JsonPropertyName("paragraphs")]
    public List<Paragraph> Paragraphs { get; set; } = [];
}
