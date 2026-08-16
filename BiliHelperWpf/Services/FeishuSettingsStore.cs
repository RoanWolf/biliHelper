using System;
using System.IO;
using System.Text.Json;
using BiliHelperWpf.Models;

namespace BiliHelperWpf.Services;

/// <summary>
/// 飞书同步设置的本地持久化（明文，与 cookie 同一目录同一安全级别）。
/// </summary>
public static class FeishuSettingsStore
{
    private static readonly string FilePath;

    static FeishuSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliHelper");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "feishu_settings.json");
    }

    /// <summary>读取已保存的设置；文件缺失或解析失败则返回默认（关闭态）。</summary>
    public static FeishuSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<FeishuSettings>(json);
                if (s != null)
                {
                    if (string.IsNullOrWhiteSpace(s.RootFolder))
                        s.RootFolder = "BiliHelper";
                    return s;
                }
            }
        }
        catch { /* 解析失败回退默认 */ }
        return new FeishuSettings();
    }

    /// <summary>保存设置到本地文件。</summary>
    public static void Save(FeishuSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* 写失败不崩溃 */ }
    }
}
