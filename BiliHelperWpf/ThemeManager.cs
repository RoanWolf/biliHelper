using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace BiliHelperWpf;

/// <summary>
/// 管理主题（浅色 / 深色）切换。
/// 通过在运行时替换 App 级合并字典的 Source，触发所有 DynamicResource 刷新。
/// </summary>
public static class ThemeManager
{
    public const string LightThemeUri = "Themes/Light.xaml";
    public const string DarkThemeUri = "Themes/Dark.xaml";

    public static bool IsDark { get; private set; }

    /// <summary>
    /// 切换主题并持久化选择。
    /// </summary>
    public static void Toggle()
    {
        ApplyDark(!IsDark);
    }

    /// <summary>
    /// 应用指定主题（true=深色）。
    /// </summary>
    public static void ApplyDark(bool dark)
    {
        if (dark == IsDark)
        {
            // 首次启动 IsDark=false 而实际是 Light，跳过 apply，仅保存选择
            Persistence.Save(dark);
            return;
        }

        IsDark = dark;
        ReplaceTheme(dark ? DarkThemeUri : LightThemeUri);
        Persistence.Save(dark);
    }

    /// <summary>
    /// 在 App 启动后调用，恢复上次保存的主题选择。
    /// </summary>
    public static void LoadPersisted()
    {
        bool saved = Persistence.TryGetDark();
        if (saved != IsDark)
        {
            IsDark = saved;
            ReplaceTheme(saved ? DarkThemeUri : LightThemeUri);
        }
    }

    private static void ReplaceTheme(string uri)
    {
        try
        {
            if (Application.Current?.Resources is not { } res)
                return;

            var theme = res.MergedDictionaries;
            // 定位"主题字典"：Source 文件名恰好是 Light.xaml / Dark.xaml 的那一个。
            // 不能用 /Themes/ 结尾或 index[0] 判断（MergedDictionaries 里还有 ScrollBar.xaml 等共享字典）。
            var target = theme.FirstOrDefault(d
                => d.Source is { } src
                && Regex.IsMatch(src.OriginalString, @"Light\.xaml$|Dark\.xaml$", RegexOptions.IgnoreCase));

            if (target == null)
            {
                // 主题字典缺失（异常场景）：直接新增，避免误改共享字典。
                theme.Add(new ResourceDictionary { Source = new Uri(uri, UriKind.Relative) });
            }
            else
            {
                target.Source = new Uri(uri, UriKind.Relative);
            }
        }
        catch (Exception ex)
        {
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
                    return string.Equals(File.ReadAllText(FilePath).Trim(), "dark", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
            return false;
        }

        public static void Save(bool dark)
        {
            try { File.WriteAllText(FilePath, dark ? "dark" : "light"); }
            catch { }
        }
    }
}