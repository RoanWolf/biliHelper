using System;
using System.Diagnostics;
using System.IO;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BiliHelperWpf;

/// <summary>
/// 管理主题（浅色 / 深色）切换。
///
/// 内部改用 WPF-UI 的 <see cref="ApplicationThemeManager"/> 切换主题资源字典
/// （自研 Themes/ 目录与 34 画刷体系已在 WPF-UI 迁移中移除）。
/// 持久化仍写入 %LocalAppData%\BiliHelper\theme.txt（作为唯一权威，启动恢复）。
///
/// 外部调用点 API 不变：App.OnStartup → LoadPersisted()，AppearancePanel → IsDark / ApplyDark。
/// </summary>
public static class ThemeManager
{
    /// <summary>
    /// 当前是否为深色主题（从 WPF-UI 当前应用主题派生）。
    /// </summary>
    public static bool IsDark =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

    /// <summary>
    /// 切换主题（浅 <-> 深）并持久化选择。
    /// </summary>
    public static void Toggle() => ApplyDark(!IsDark);

    /// <summary>
    /// 应用指定主题（true=深色），并持久化选择。
    /// </summary>
    public static void ApplyDark(bool dark)
    {
        // 主题一致时跳过重复 apply，仅保存选择（与旧实现行为一致）
        if (dark == IsDark &&
            ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Unknown)
        {
            Persistence.Save(dark);
            return;
        }

        Apply(dark);
        Persistence.Save(dark);
    }

    /// <summary>
    /// 在 App 启动后调用，恢复上次保存的主题选择。
    /// </summary>
    public static void LoadPersisted()
    {
        bool saved = Persistence.TryGetDark();
        if (saved == IsDark &&
            ApplicationThemeManager.GetAppTheme() != ApplicationTheme.Unknown)
            return;

        Apply(saved);
    }

    /// <summary>
    /// 通过 WPF-UI 切换主题资源字典。
    /// </summary>
    /// <param name="dark">true=深色，false=浅色。</param>
    /// <remarks>
    /// backdrop 固定传 <see cref="WindowBackdropType.None"/>：Phase 2 启用 Mica 时
    /// 再改为对应 backdrop（届时需配合 FluentWindow 的 ExtendsContentIntoTitleBar=True）。
    /// updateAccent=true：让 WPF-UI 同步更新 accent 颜色资源（跟随系统强调色）。
    /// </remarks>
    private static void Apply(bool dark)
    {
        try
        {
            ApplicationThemeManager.Apply(
                dark ? ApplicationTheme.Dark : ApplicationTheme.Light,
                WindowBackdropType.None,
                updateAccent: true);

            Debug.WriteLine($"[ThemeManager] 主题已切换为 {(dark ? "深色" : "浅色")}");
        }
        catch (Exception ex)
        {
            // 主题切换失败不崩溃，保留日志便于排查
            Debug.WriteLine($"[ThemeManager] 主题切换失败: {ex}");
        }
    }

    /// <summary>
    /// 主题选择的轻量持久化（存储在 %LocalAppData%\BiliHelper\theme.txt）。
    /// </summary>
    private static class Persistence
    {
        private static readonly string FilePath;

        static Persistence()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BiliHelper");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "theme.txt");
        }

        public static bool TryGetDark()
        {
            try
            {
                if (File.Exists(FilePath))
                    return string.Equals(
                        File.ReadAllText(FilePath).Trim(),
                        "dark",
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // 读取失败按浅色处理
            }
            return false;
        }

        public static void Save(bool dark)
        {
            try
            {
                File.WriteAllText(FilePath, dark ? "dark" : "light");
            }
            catch
            {
                // 写入失败忽略（不阻塞主题切换）
            }
        }
    }
}
