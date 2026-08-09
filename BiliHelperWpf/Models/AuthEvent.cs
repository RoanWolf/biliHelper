namespace BiliHelperWpf.Models;

/// <summary>
/// 本地 B 站 cookie 的接入状态（驱动主界面 🍪 / ⚠ 图标）。
/// </summary>
public enum CookieState
{
    /// <summary>无本地 cookie 记录（未登录）。</summary>
    None,

    /// <summary>有效（可拉取字幕）。</summary>
    Valid,

    /// <summary>已过期 / 需刷新 / 探测失败。</summary>
    Invalid,
}

/// <summary>
/// 扫码登录过程事件，对应 auth.py 推送的每一行 JSON。
/// </summary>
public class AuthEvent
{
    public string Type { get; set; } = "";

    // ── qr ──
    public string? Url { get; set; }
    public string? QrKey { get; set; }

    /// <summary>二维码 PNG 的 base64（不再落盘 qr_login.png，避免并发写坏）。</summary>
    public string? ImageBase64 { get; set; }

    // ── status ──
    public int Code { get; set; }

    // ── user ──
    public string? Uname { get; set; }
    public string? Face { get; set; }
    public long Mid { get; set; }

    // ── check ──
    public string? State { get; set; }
    public string? Message { get; set; }

    // ── success ──
    public int Count { get; set; }
    public string? RefreshToken { get; set; }

    // ── ok / error ──
    public bool Ok { get; set; }
    public bool Deleted { get; set; }
    public bool Refreshed { get; set; }
}
