using System;
using System.IO;
using System.Text.Json;
using BiliHelperWpf.Models;

namespace BiliHelperWpf.Services;

/// <summary>
/// AI 大模型连接设置的本地持久化（含 API key，明文存储，与 cookie 同一目录同一安全级别）。
/// 零 NuGet 约定下不做加密。
/// </summary>
public static class AiSettingsStore
{
    private static readonly string FilePath;

    static AiSettingsStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BiliHelper");
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, "ai_settings.json");
    }

    /// <summary>读取已保存的设置；文件缺失则返回空设置（所有字段为空）。</summary>
    public static AiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AiSettings>(json) ?? new AiSettings();
            }
        }
        catch { /* 解析失败回退为空设置 */ }
        return new AiSettings();
    }

    /// <summary>保存设置到本地文件。</summary>
    public static void Save(AiSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch { /* 写失败不崩溃 */ }
    }
}