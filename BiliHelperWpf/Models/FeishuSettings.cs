namespace BiliHelperWpf.Models;

/// <summary>
/// 飞书同步设置，持久化到 %LocalAppData%\BiliHelper\feishu_settings.json
/// （与 cookie/ai_settings 同一目录同一安全级别）。
/// </summary>
public class FeishuSettings
{
    /// <summary>是否启用飞书同步（AI 整理成功后自动同步）。</summary>
    public bool Enabled { get; set; }

    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string ChatId { get; set; } = "";

    /// <summary>飞书云盘顶层文件夹名，默认 "BiliHelper"。</summary>
    public string RootFolder { get; set; } = "BiliHelper";

    /// <summary>配置是否齐备（启用 + 三要素非空）。</summary>
    public bool IsComplete =>
        Enabled
        && !string.IsNullOrWhiteSpace(AppId)
        && !string.IsNullOrWhiteSpace(AppSecret)
        && !string.IsNullOrWhiteSpace(ChatId);
}
