namespace BiliHelperWpf.Models;

/// <summary>
/// AI 大模型连接设置（API key / base_url / model），持久化到
/// %LocalAppData%\BiliHelper\ai_settings.json（与 cookie/theme 同一位置）。
/// </summary>
public class AiSettings
{
    public string ApiKey { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string Model { get; set; } = "";

    public bool HasKey => !string.IsNullOrWhiteSpace(ApiKey);
}