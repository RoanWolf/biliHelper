using System.Collections.ObjectModel;

namespace BiliHelperWpf.Models;

/// <summary>
/// 历史记录按日期分组后的分组模型。
/// 用于历史抽屉中展示：
///   2026年07月28日
///     ├─ 视频1
///     └─ 视频2
/// </summary>
public class HistoryGroup
{
    /// <summary>
    /// 分组标题，如 "2026年07月28日"。
    /// </summary>
    public string GroupTitle { get; init; } = string.Empty;

    /// <summary>
    /// 排序用的日期键，格式 "YYYYMMDD"。
    /// </summary>
    public string DateKey { get; init; } = string.Empty;

    /// <summary>
    /// 该日期下的历史记录列表（按时间倒序）。
    /// </summary>
    public ObservableCollection<HistoryItem> Items { get; init; } = [];
}
