namespace BiliHelperWpf.Models;

/// <summary>
/// 单条字幕条目。
/// </summary>
public class SubtitleEntry
{
    public int Index { get; init; }
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// 格式化后的时间字符串，如 "00:03.24"。
    /// </summary>
    public string StartTimeFormatted => FormatTime(StartTime);
    public string EndTimeFormatted => FormatTime(EndTime);

    /// <summary>
    /// 字幕持续时长（秒）。
    /// </summary>
    public double Duration => EndTime - StartTime;

    private static string FormatTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{ts.Hours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
    }
}
