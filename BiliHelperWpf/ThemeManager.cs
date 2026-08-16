using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
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
    /// 主题切换完成后触发（含启动恢复与覆盖资源重注入），参数为当前是否深色。
    /// 供标题栏 logo 等主题感知元素订阅刷新。
    /// </summary>
    public static event Action<bool>? ThemeChanged;

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
            // 覆盖资源仍需同步（启动/切换时可能未注入过）
            ApplyThemeOverrides(dark);
            ThemeChanged?.Invoke(dark);
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
        {
            // 主题一致时虽无需重复 apply，但浅色/深色覆盖资源必须补注入
            // （此前覆盖只能在 Apply() 路径触发——启动时主题一致会跳过，导致浅色选中态覆盖从不生效）
            ApplyThemeOverrides(saved);
            ThemeChanged?.Invoke(saved);
            return;
        }

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

            // 按当前主题类型注入/移除覆盖资源（深色注入灰背景，浅色淡化选中态）
            ApplyThemeOverrides(dark);

            // 通知主题感知元素（标题栏 logo 等）刷新
            ThemeChanged?.Invoke(dark);
        }
        catch (Exception ex)
        {
            // 主题切换失败不崩溃，保留日志便于排查
            Debug.WriteLine($"[ThemeManager] 主题切换失败: {ex}");
        }
    }

    /// <summary>
    /// 深色主题下注入更灰的背景覆盖资源（写入应用资源顶层，覆盖 WPF-UI 深色默认值）。
    /// 切回浅色时 Remove，恢复 WPF-UI 默认。
    /// 注意：ApplicationThemeManager.Apply 只替换 wpf.ui 命名空间下的字典，应用资源顶层
    /// 的这些 key 会保留到下次主题切换，因此深色下注入后持续生效；浅色时必须移除，
    /// 否则会误伤浅色主题。
    /// </summary>
    private static void ApplyDarkOverrides(bool dark)
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources == null)
            return;

        if (dark)
        {
            // 统一使用不透明的柔和蓝灰。WPF-UI 深色默认是极淡半透明白/黑叠加，
            // 在 WindowBackdropType=None（无 Mica）下叠出死黑——全部改为不透明浅灰。
            SolidColorBrush Solid(byte r, byte g, byte b)
            {
                var brush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(r, g, b));
                brush.Freeze();
                return brush;
            }

            // 窗口 / 页面 / 标题栏底色（TitleBar 背景透明，露出的就是这里）
            var bg = Solid(0x38, 0x3C, 0x42);
            resources["ApplicationBackgroundBrush"] = bg;
            resources["WindowBackground"] = bg;
            resources["SolidBackgroundFillColorBaseBrush"] = bg;

            // 卡片表面（分P区 / URL行 / 进度行 / 元信息 / 搜索栏 / 底栏 / 错误行 / 抽屉标题）
            resources["CardBackgroundFillColorDefaultBrush"] = Solid(0x3F, 0x43, 0x4A);
            resources["CardBackgroundFillColorSecondaryBrush"] = Solid(0x46, 0x4A, 0x51);
            resources["CardBackground"] = Solid(0x3F, 0x43, 0x4A);       // 控件模板兜底
            resources["CardBackgroundPointerOver"] = Solid(0x4D, 0x51, 0x58);

            // 分层 / 导航表面
            resources["LayerFillColorDefaultBrush"] = Solid(0x3B, 0x3F, 0x45);
            resources["LayerFillColorAltBrush"] = Solid(0x42, 0x46, 0x4C);

            // 控件填充（按钮 / Tab / 悬停 / 按下）
            resources["ControlFillColorDefaultBrush"] = Solid(0x45, 0x49, 0x50);
            resources["ControlFillColorSecondaryBrush"] = Solid(0x4D, 0x51, 0x58); // 设置窗导航栏背景
            resources["ControlFillColorTertiaryBrush"] = Solid(0x55, 0x59, 0x60);
            resources["ControlFillColorInputActiveBrush"] = Solid(0x3A, 0x3E, 0x45);

            // 替代填充（列表选中 / 悬停底）
            resources["ControlAltFillColorSecondaryBrush"] = Solid(0x4A, 0x4E, 0x55);
            resources["ControlAltFillColorTertiaryBrush"] = Solid(0x52, 0x56, 0x5D);
            resources["ControlAltFillColorQuarternaryBrush"] = Solid(0x56, 0x5A, 0x61);
            resources["SubtleFillColorSecondaryBrush"] = Solid(0x3D, 0x41, 0x47);
            resources["SubtleFillColorTertiaryBrush"] = Solid(0x45, 0x49, 0x50);

            // 输入框背景（比卡片略深，保持层次）
            resources["TextControlBackground"] = Solid(0x31, 0x35, 0x3B);
            resources["TextControlBackgroundPointerOver"] = Solid(0x38, 0x3C, 0x42);
            resources["TextControlBackgroundFocused"] = Solid(0x3A, 0x3E, 0x45);

            // WPF-UI 控件模板引用的专用 brush（优先级高于通用 fill key）
            resources["ButtonBackground"] = Solid(0x45, 0x49, 0x50);
            resources["ButtonBackgroundPointerOver"] = Solid(0x4D, 0x51, 0x58);
            resources["ButtonBackgroundPressed"] = Solid(0x55, 0x59, 0x60);
            resources["ListBoxItemSelectedBackgroundThemeBrush"] = Solid(0x4A, 0x4E, 0x55);
            // 选中项前景：WPF-UI 深色默认 TextOnAccentFillColorPrimary=黑（配 accent 亮底），
            // 但选中背景已被覆盖为深灰 → 黑配黑。这里强制改为白字。
            resources["ListBoxItemSelectedForegroundThemeBrush"] = Solid(0xFF, 0xFF, 0xFF);
            resources["NavigationViewItemBackgroundPointerOver"] = Solid(0x3D, 0x41, 0x47);
            resources["NavigationViewItemBackgroundSelected"] = Solid(0x4A, 0x4E, 0x55);

            // 描边：深色下默认 #12FFFFFF 几乎不可见，提亮让卡片/分P 区域有清晰边界
            resources["ControlStrokeColorDefaultBrush"] = Solid(0x56, 0x5A, 0x61);
            resources["CardStrokeColorDefaultBrush"] = Solid(0x56, 0x5A, 0x61);

            // accent 底上的文字：WPF-UI 深色默认 TextOnAccentFillColorPrimary=黑（配亮 accent 底），
            // 但设置页导航选中/主题选择卡/主按钮等用 accent 蓝底 → 黑字配蓝底（黑配黑）。强制改白字。
            resources["TextOnAccentFillColorPrimaryBrush"] = Solid(0xFF, 0xFF, 0xFF);
            // Primary 按钮前景（ui:Button Appearance="Primary"）源自 TextOnAccent，深色下改白
            resources["AccentButtonForeground"] = Solid(0xFF, 0xFF, 0xFF);
            resources["AccentButtonForegroundPointerOver"] = Solid(0xFF, 0xFF, 0xFF);

            // Tab 选中背景：下方已有蓝色指示条表达选中，深色下 TabItem 模板默认的
            // 深灰选中底（TabViewItemHeaderBackgroundSelected）会"加黑"标签，改为透明。
            resources["TabViewItemHeaderBackgroundSelected"] = System.Windows.Media.Brushes.Transparent;
        }
        else
        {
            resources.Remove("ApplicationBackgroundBrush");
            resources.Remove("WindowBackground");
            resources.Remove("SolidBackgroundFillColorBaseBrush");
            resources.Remove("CardBackgroundFillColorDefaultBrush");
            resources.Remove("CardBackgroundFillColorSecondaryBrush");
            resources.Remove("CardBackground");
            resources.Remove("CardBackgroundPointerOver");
            resources.Remove("LayerFillColorDefaultBrush");
            resources.Remove("LayerFillColorAltBrush");
            resources.Remove("ControlFillColorDefaultBrush");
            resources.Remove("ControlFillColorSecondaryBrush");
            resources.Remove("ControlFillColorTertiaryBrush");
            resources.Remove("ControlFillColorInputActiveBrush");
            resources.Remove("ControlAltFillColorSecondaryBrush");
            resources.Remove("ControlAltFillColorTertiaryBrush");
            resources.Remove("ControlAltFillColorQuarternaryBrush");
            resources.Remove("SubtleFillColorSecondaryBrush");
            resources.Remove("SubtleFillColorTertiaryBrush");
            resources.Remove("TextControlBackground");
            resources.Remove("TextControlBackgroundPointerOver");
            resources.Remove("TextControlBackgroundFocused");
            resources.Remove("ButtonBackground");
            resources.Remove("ButtonBackgroundPointerOver");
            resources.Remove("ButtonBackgroundPressed");
            resources.Remove("ListBoxItemSelectedBackgroundThemeBrush");
            resources.Remove("ListBoxItemSelectedForegroundThemeBrush");
            resources.Remove("NavigationViewItemBackgroundPointerOver");
            resources.Remove("NavigationViewItemBackgroundSelected");
            resources.Remove("ControlStrokeColorDefaultBrush");
            resources.Remove("CardStrokeColorDefaultBrush");
            resources.Remove("TextOnAccentFillColorPrimaryBrush");
            resources.Remove("AccentButtonForeground");
            resources.Remove("AccentButtonForegroundPointerOver");
            resources.Remove("TabViewItemHeaderBackgroundSelected");
        }
    }

    /// <summary>
    /// 统一入口：按当前主题类型注入/移除覆盖资源。
    /// 供 Apply() 与"主题一致跳过"路径共用，确保覆盖总能生效。
    /// </summary>
    private static void ApplyThemeOverrides(bool dark)
    {
        ApplyDarkOverrides(dark);
        ApplyLightOverrides(dark);
    }

    private static void ApplyLightOverrides(bool dark)
    {
        var resources = System.Windows.Application.Current?.Resources;
        if (resources == null)
            return;

        if (!dark)
        {
            // 从当前系统 accent 色（WPF-UI 运行时注入）派生淡化选中色，
            // 不硬编码蓝色：随系统强调色走，且比默认实心 accent 底轻盈
            var accent = System.Windows.Media.Color.FromRgb(0x0A, 0x7A, 0xFF); // 兜底默认蓝
            var accentResource = System.Windows.Application.Current?.TryFindResource("AccentFillColorDefaultBrush");
            if (accentResource is System.Windows.Media.SolidColorBrush accentBrush && accentBrush is not null)
            {
                accent = accentBrush.Color;
            }

            // 选中底：accent 色 6% 透明 → 更淡的蓝底（替代默认实心深蓝底）
            var selectedBg = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x0F, accent.R, accent.G, accent.B));
            selectedBg.Freeze();
            resources["ListBoxItemSelectedBackgroundThemeBrush"] = selectedBg;

            // 选中文字：accent 与白色 5:5 混合 → 浅蓝，不再刺眼
            var lighterFg = System.Windows.Media.Color.FromArgb(
                0xFF,
                (byte)((accent.R + 0xFF) / 2),
                (byte)((accent.G + 0xFF) / 2),
                (byte)((accent.B + 0xFF) / 2));
            var selectedFg = new System.Windows.Media.SolidColorBrush(lighterFg);
            selectedFg.Freeze();
            resources["ListBoxItemSelectedForegroundThemeBrush"] = selectedFg;
        }
        else
        {
            // 深色时不移除选中态 key——这两个 key 由 ApplyDarkOverrides 管理
            // （深色=深灰底+白字）。这里若 Remove，会删掉深色白字，
            // 回落到 WPF-UI 深色默认黑字 → 黑配黑。
            // 切回浅色时 ApplyDarkOverrides(dark=false) 会负责移除它们。
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
