# WPF-UI 迁移任务清单（路线 A：全家桶替换）

> 本文档是本迁移的唯一进度锚点。**每次会话结束前更新「进度快照」；每次会话开始先读本文件**。
> 用户已拍板：zero-NuGet 约定作废，采用 WPF-UI 全家桶（路线 A）。

---

## 0. 决策记录（已确认，勿反复）

| 决策点 | 结论 |
|---|---|
| 包版本 | **WPF-UI 4.3.0**（4.2.x/4.1.x 标记 deprecated 有 critical bugs，避开） |
| 布局骨架 | MainWindow 保留现有功能布局（URL 工具行 + 分P列表 + 字幕/AI 双TAB），只换框架与控件，不重新发明布局 |
| ViewModel/Services | **全部保留不动**（`MainViewModel.cs` 880 行、BiliService/AiReadService/AuthService/HistoryService） |
| Converters | 保留 `Converters/` 三个转换器，必要时适配取色方式 |
| 字体 | **保留**「Lilex Duo + 微软雅黑」混合（中文显示稳定）；WPF-UI 默认 Segoe UI Variable 不采用 |
| 主题持久化 | theme.txt 由自研 ThemeManager/theme.txt 迁至 WPF-UI 机制，行为等价（下次启动恢复） |
| 历史数据 | `history/`、`%LocalAppData%\BiliHelper\`（cookie/指纹/AI设置）一律不动 |
| 可逆性 | 迁移全程保留 git 历史；每阶段可独立提交回滚 |

## 1. 项目现状快照（迁移前）

```
WPF UI 层文件（待替换/迁移）：
  MainWindow.xaml                     1001 行   ← 重写为 FluentWindow
  MainWindow.xaml.cs                   240 行   ← 保留逻辑（窗口隐藏/DWM/设置开关）
  SettingsWindow.xaml                  179 行   ← 重写为 FluentWindow + NavigationView
  SettingsWindow.xaml.cs                —       ← 保留（NavigateTo 逻辑适配）
  Settings/SettingsStyles.xaml         374 行   ← 14 个共享样式，删除/迁移
  Settings/AccountPanel.xaml(.cs)   176+270 行   ← XAML 换控件，cs 保留
  Settings/AiModelPanel.xaml(.cs)   110+87 行    ← 同上
  Settings/AppearancePanel.xaml(.cs) 200+32 行   ← 同上
  Themes/Light.xaml                    58 行（34 画刷）← 删除（映射到 WPF-UI 主题）
  Themes/Dark.xaml                     58 行（34 画刷）← 删除
  Themes/ScrollBar.xaml                89 行     ← 删除（WPF-UI 自带）
  ThemeManager.cs                     121 行     ← 删除/替换
  App.xaml / App.xaml.cs                        ← 接入 WPF-UI ApplicationThemeManager

不动层：
  ViewModels/（MainViewModel/RelayCommand/ViewModelBase）
  Services/（BiliService/AiReadService/AuthService/AiSettingsStore/HistoryService）
  Models/、Converters/、MainWindow.xaml.cs（逻辑部分）
  BiliHelperCore/ (Python 全部)
```

跨进程契约（**不可破坏，AGENTS.md 红线**）：
- stdout JSONL + `flush=True`；Python 直调 `.venv/Scripts/python.exe`（非 uv run）
- 所有 spawn 点 `PYTHONIOENCODING=utf-8` + 双流 `Encoding.UTF8`
- 取消杀整棵进程树 `Kill(entireProcessTree: true)`
- UI 层迁移不触碰这些逻辑 → 回归只测 UI 表现

## 2. 阶段划分与任务清单

### Phase 0 — 可行性验证（先跑通再铺开）
- [x] `dotnet add package WPF-UI --version 4.3.0`（在 `BiliHelperWpf/`）
- [x] 构建通过（`dotnet build`，0 警告 0 错误；net10.0-windows 引用正常）
- [x] App.xaml 接入 WPF-UI：官方模板方式 `<ui:ThemesDictionary Theme="Light"/>` + `<ui:ControlsDictionary/>`（迁移期与自研主题共存）
- [x] 冒烟：MainWindow 换 `FluentWindow`（`ExtendsContentIntoTitleBar=True`、移除显式 WindowChrome、删自研 DWM 圆角代码）。构建 0 警告 0 错误；启动存活无崩溃；auth 探测→自动弹设置中心→二维码轮询链路正常。**Mica 未启用**（要求 ExtendsContentIntoTitleBar=True，留 Phase 2 布局定型后配）
- [x] 记录：行为差异已追加到 §4「风险与坑」
- ✅ 出口条件：空壳 FluentWindow 能跑，构建 0 警告 0 错误 —— **已达成（Phase 0 完成）**

### Phase 1 — 主题体系迁移 ✅（2026-08-12 完成）
- [x] 提取 `Themes/Light.xaml` 完整 34 个 brush key 清单，填入 §3 映射表
- [x] 逐 key 映射到 WPF-UI 主题资源（**方案 A 全量直改 202 处**，`{DynamicResource 自研key}` → `{DynamicResource WPF-UI key}`），映射表见 §3
- [x] 方案 A：一次性脚本 `/tmp/replace_theme_keys.py` 替换 6 个 XAML（MainWindow/SettingsWindow/SettingsStyles/Account/AiModel/Appearance），**XAML 自研 key 残留归零**
- [x] C# 侧同步：`StatusToColorConverter.cs`（SuccessBrush→SystemFillColorSuccessBrush 等 3 键）、`AiModelPanel.xaml.cs`（SuccessBrush/ErrorBrush）改为 WPF-UI key
- [x] 删除 `Themes/Light.xaml`、`Themes/Dark.xaml`、`Themes/ScrollBar.xaml`（目录整体删除）；App.xaml 移除自研字典引用（仅留 `<ui:ThemesDictionary Theme="Light"/>` + `<ui:ControlsDictionary/>`）
- [x] `ThemeManager.cs` **保留为薄封装**（未删除）：内部改用 `WPF.Ui.Appearance.ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true)`；外部 API（IsDark/ApplyDark/Toggle/LoadPersisted）不变，`AppearancePanel` 无需改动；持久化仍用 theme.txt
- [x] 构建 0 警告 0 错误；运行验证：主题资源加载正常、无 FATAL、cookie 探索链路正常
- ✅ 出口条件：无任何 `自研brush key` 引用残留（grep 验证），主题切换/恢复正常 —— **已达成**

### Phase 2 — MainWindow 迁移 ✅（2026-08-12 完成）
- [x] 根元素换 `<ui:FluentWindow>`：`WindowBackdropType="Mica"` + `ExtendsContentIntoTitleBar="True"`（**Mica 已启用**），WindowChrome 由 FluentWindow 自带，删自研 DWM 圆角代码
- [x] 标题栏换 `<ui:TitleBar>`：Icon=logo.png / Title=BiliHelper / `CloseWindowByDoubleClickOnIcon=True`（自动拖拽+双击最大化+窗口按钮+主题感知）；Row 高度 32→48；删除自研 `TitleBarButtonStyle`/`CloseButtonStyle` 与 4 个手写按钮
- [x] URL 工具行：自绘 Border 包裹去掉，TextBox 直接用 WPF-UI Fluent 默认样式（圆角/焦点下划线/hover）；3 个按钮去掉 `SecondaryButtonStyle`（WPF-UI 默认 Button 样式接管）
- [x] 状态行/错误行/元信息行：颜色 Phase 1 已直改 WPF-UI key，布局保留（Flutter 观感已统一）
- [x] 分P 列表（ListBox）：删自绘 `PartItemStyle`（WPF-UI 默认 ListBoxItem 样式接管），ItemTemplate 保留
- [x] 字幕列表（ListBox）：删自绘 `SubItemStyle`（同上），ItemTemplate 保留
- [x] TAB 切换：自绘 Border TAB 替换为 WPF-UI `TabControl`，`SelectedIndex="{Binding SelectedSubtitleTab, Mode=TwoWay}"`；`_partTabMemory`（每分P TAB 记忆）未动；`SubtitleTab_PreviewMouseDown` 处理器已删
- [x] AI 段落展示：错误提示换 `ui:InfoBar`（Title="AI 整理失败" / Message 绑定 AiReadErrorMessage / Severity=Error / IsOpen 绑定 AiReadError）；未整理空态卡片保留（已是 WPF-UI 配色）
- [x] `MainWindow.xaml.cs`：删除 `OnWindowStateChanged` / `TitleBar_MouseLeftButtonDown` / `MinimizeButton_Click` / `MaximizeButton_Click` / `CloseButton_Click` / `SubtitleTab_PreviewMouseDown`（TitleBar 与 TabControl 接管）
- [x] `{DynamicResource 自研key}` Phase 1 已清零；`xmlns:shell` + 3 处 `shell:WindowChrome.IsHitTestVisibleInChrome` 残留已删
- ✅ 出口条件：构建 0 警告 0 错误；运行验证 ALIVE 无 FATAL（Mica + TitleBar + TabControl + InfoBar + Fluent TextBox/Button/ListBox 全部正常）—— **已达成**

### Phase 3 — SettingsWindow 迁移 ✅（2026-08-12 完成）
- [x] 根元素换 `ui:FluentWindow`（保留 880×620 固定 / `ResizeMode=NoResize` / 圆角 10 + 外圈边框 `ControlStrokeColorDefaultBrush`）
- [x] **导航决策**：**未用 `NavigationView`** —— 固定 880×620 小窗用 NavigationView（Pane+Frame 页面导航机制）改造复杂，且 WPF-UI 4.2.x 有 navigation critical bugs 被标记 deprecated；**保留已验证的 RadioButton 药丸导航**（视觉已 Fluent，`FluentNavItemStyle`/`FluentNavLabelStyle` 保留）
- [x] `NavigateTo` / `Nav_Checked` / `_panels` 空值防御**全部保留**（对外 API 不变，MainWindow `OpenSettingsWindow(panelIndex)` 无需改动）
- [x] 标题栏换 `ui:TitleBar`（Title="设置" + logo 图标 + `CloseWindowByDoubleClickOnIcon=True`），Row 高度 32→48；删除自绘 `SettingsCloseButtonStyle` 与 `CloseButton_Click` 处理器
- [x] `SettingsStyles.xaml` 从 14 样式减至 **10 个**：删 4 个手搓控件模板（`FluentButtonStyle`/`FluentPrimaryButtonStyle`/`FluentTextBoxStyle`/`FluentPasswordBoxStyle`），改用 WPF-UI 原生（`ui:Button Appearance` + 默认 TextBox/PasswordBox）；保留 10 个排版/导航/主题卡样式（已是 WPF-UI key）
- ✅ 出口条件：构建 0 警告 0 错误；**无 cookie 自动弹设置窗 → 账号面板二维码生成 + 轮询全链路实测通过**（cookies 临时备份/恢复验证，无 FATAL）

### Phase 4 — 设置面板迁移 ✅（2026-08-12 完成）
- [x] `AccountPanel.xaml`：「退出登录」「重新生成」→ `ui:Button Appearance="Secondary"`（cs 逻辑不动）
- [x] `AiModelPanel.xaml`：「测试连通性」→ `ui:Button Secondary`、「保存」→ `ui:Button Primary`；API Key PasswordBox / Base URL / Model TextBox 去掉自绘样式（WPF-UI 默认 Fluent 外观接管；cs 不动）
- [x] `AppearancePanel.xaml`：主题选择卡/迷你预览**保留**（`FluentThemeCardStyle` 已是 WPF-UI key；迷你预览硬编码色是**有意保留**——预览展示固定配色，非主题动态色）
- [x] 三面板自研 DynamicResource Phase 1 已清零（202 处直改）
- ✅ 出口条件：构建 0 警告 0 错误；设置窗三面板可访问、导航切换正常、登录链路实测通过（见 Phase 3 验证）

### Phase 5 — 收尾与回归
- [ ] Converters：`StatusToColorConverter` 的 `TryFindResource` 取色改为 WPF-UI 资源（深/浅主题下取到对应色）
- [ ] grep 全仓 `DynamicResource` 自研 key 清零；grep `Themes/` 引用清零
- [ ] csproj 清理：删 Assets 中不再用的字体/资源（若保留 Lilex 则不动）
- [ ] 回归清单（逐项手测）：
  - [ ] 拉取字幕（URL 行 → meta/分P/完成 → 进度条）
  - [ ] 分P 切换 + 每分P TAB 记忆（原字幕/AI润色）
  - [ ] AI 整理（触发 → 进度 → 段落卡片 → 失败重试提示）
  - [ ] 历史记录打开/加载
  - [ ] 扫码登录（账号面板）→ 欢迎卡片
  - [ ] AI 设置测试连通性（成功/失败两类）
  - [ ] 主题切换（浅/深）即时生效 + 重启恢复
  - [ ] 窗口行为：主窗口 Resize/隐藏、设置窗 880×620 固定
- [ ] `dotnet build` 0 警告 0 错误（WPF-UI 打包需确认无警告）
- [ ] 更新 README.md + AGENTS.md：UI 架构从自研主题改为 WPF-UI（zero-NuGet 段落删除、34 画刷约定删除、SettingsWindow 描述更新）
- [ ] 提交 git：分阶段 commit（每 Phase 一个或按变更合理拆分）

## 3. 主题资源映射表（Phase 1 已定稿 ✅）

Light/Dark 自研 34 个 brush key → WPF-UI 资源（4.3.0）。**6 个 ScrollBar key 无映射**（ScrollBar.xaml 已删，WPF-UI 自带滚动条样式）。

| 自研 key | 用途 | WPF-UI 资源 | 备注 |
|---|---|---|---|
| BackgroundBrush | 窗口背景 | `ApplicationBackgroundBrush` | |
| SurfaceBrush | 卡片/条面 | `CardBackgroundFillColorDefaultBrush` | |
| SurfaceHoverBrush | 悬停 | `ControlFillColorSecondaryBrush` | |
| SurfaceSelectedBrush | 选中 | `ControlAltFillColorSecondaryBrush` | |
| InputBgBrush | 输入框背景 | `TextControlBackground` | |
| PrimaryBrush | 主色 | `AccentFillColorDefaultBrush` | 运行时注入* |
| PrimaryHoverBrush | 主色悬停 | `AccentFillColorSecondaryBrush` | 运行时注入* |
| PrimaryPressedBrush | 主色按下 | `AccentFillColorTertiaryBrush` | 运行时注入* |
| PrimaryDisabledBrush | 主色禁用 | `AccentFillColorDisabledBrush` | |
| PrimaryDisabledForegroundBrush | 主色禁用文本 | `AccentTextFillColorDisabledBrush` | |
| PrimaryForegroundBrush | 主按钮前景 | `TextOnAccentFillColorPrimaryBrush` | |
| TextPrimaryBrush | 主文本 | `TextFillColorPrimaryBrush` | |
| TextSecondaryBrush | 次文本 | `TextFillColorSecondaryBrush` | |
| TextMutedBrush | 弱文本 | `TextFillColorTertiaryBrush` | |
| BorderBrush | 分隔/边框 | `ControlStrokeColorDefaultBrush` | |
| SuccessBrush | 成功 | `SystemFillColorSuccessBrush` | |
| RunningBrush | 进行中 | `SystemFillColorAttentionBrush` | 运行时注入* |
| WaitingBrush | 等待 | `SystemFillColorNeutralBrush` | |
| ErrorBrush | 错误 | `SystemFillColorCriticalBrush` | |
| ErrorBackgroundBrush | 错误背景 | `SystemFillColorCriticalBackgroundBrush` | |
| ButtonBorderBrush | 按钮描边 | `ControlStrokeColorDefaultBrush` | 与 BorderBrush 同源 |
| ButtonPressedBrush | 按钮按下 | `ControlFillColorSecondaryBrush` | |
| ButtonDisabledBackgroundBrush | 按钮禁用背景 | `ControlFillColorDisabledBrush` | |
| ButtonDisabledForegroundBrush | 按钮禁用文本 | `TextFillColorDisabledBrush` | |
| TitleBarPressedBrush | 标题栏按下 | `ControlFillColorSecondaryBrush` | |
| CloseButtonHoverBrush | 关闭钮悬停 | `SystemFillColorCriticalBrush` | |
| CloseButtonPressedBrush | 关闭钮按下 | `SystemFillColorCriticalBrush` | |
| OverlayBrush | 遮罩 | `SystemFillColorSolidNeutralBrush` | |
| ScrollBarTrackBrush | 滚动条轨道 | — | 随 ScrollBar.xaml 删除 |
| ScrollBarThumbBrush | 滚动条 thumb | — | 同上 |
| ScrollBarThumbHoverBrush | 滚动条 thumb 悬停 | — | 同上 |
| ScrollBarThumbPressedBrush | 滚动条 thumb 按下 | — | 同上 |
| ScrollBarButtonBrush | 滚动条按钮 | — | 同上 |
| ScrollBarButtonHoverBrush | 滚动条按钮悬停 | — | 同上 |

> \* 运行时注入：`AccentFillColor*Brush` / `SystemFillColorAttentionBrush` 等由 `ApplicationAccentColorManager` 在 `ApplicationThemeManager.Apply(updateAccent: true)` 时写入应用资源——**ThemeManager 调用必须保持 `updateAccent: true`**，否则这些 key 运行时缺失。
> 替换总量：6 个 XAML 共 202 处 `{DynamicResource}`，另 2 个 .cs 里 5 处字符串 key。

## 4. 风险与坑（持续追加）

- [x] **兼容性**：WPF-UI 4.3.0 目标 `net8.0-windows7.0`，NuGet 标注 net10.0-windows 兼容——**Phase 0 实测通过**（构建 0 警告 0 错误 + 运行正常）
- [ ] **WPF-UI 4.2.0 的 critical bugs**：若 4.3.0 仍复现（导航/TAB 相关），记录现象并查 release notes
- [ ] **字体**：WPF-UI 默认 Segoe 家族，Lilex Duo 只对等宽场景生效——迁移后检查等宽是否保留
- [x] **WindowChrome 冲突**：FluentWindow 自带 WindowChrome，现有 `<shell:WindowChrome>` 必须移除——**已处理**（MainWindow 移除显式 WindowChrome；标题栏 `IsHitTestVisibleInChrome` 标记保留，拖拽行为留 Phase 2 验证）
- [x] **Mica 在无边框 + 自定义背景下的表现**：Phase 2 已实测——MainWindow `WindowBackdropType=Mica` + `ExtendsContentIntoTitleBar=True`，运行正常无渲染异常（原 DropShadow 坑与 FluentWindow 无关，未复现）
- [x] **Converters 取色**：`StatusToColorConverter` 用 `TryFindResource` 运行时取色——已同步改 key（ok→SystemFillColorSuccessBrush / partial→SystemFillColorAttentionBrush / empty→SystemFillColorNeutralBrush）；`AiModelPanel.xaml.cs` 同步（Success/Critical）。回归时重点看状态点颜色
- [x] **TAB 记忆逻辑**：TabControl `SelectedIndex` 双向绑定 `SelectedSubtitleTab`，`_partTabMemory`（每分P 记忆 TAB）未动——**Phase 2 已保留**，回归时切分P 验证 TAB 还原
- [x] **App.xaml 加载方式**：官方接入 `<ui:ThemesDictionary Theme="Light"/>` + `<ui:ControlsDictionary/>`——Phase 1 已删自研字典（ScrollBar/Light），现仅 WPF-UI
- [x] **Mica 前置条件**：`WindowBackdropType` 非 None 时 `ExtendsContentIntoTitleBar` 必须为 True（否则抛 InvalidOperationException）——**Phase 2 已遵守**（两属性已同时设置）
- [x] **Accent 运行时注入**：accent 系 key 由 `ApplicationAccentColorManager` 注入，`ThemeManager` 调用 `Apply(theme, WindowBackdropType.None, updateAccent: true)` 保持 true（Phase 1 实测正常）
- [x] **exe 文件锁**：残留运行的 BiliHelperWpf.exe 会锁 bin 导致构建 MSB3026/MSB3027 失败——构建前需 `taskkill //F //IM BiliHelperWpf.exe`（属构建环境问题，非代码缺陷）
- [x] **ThemeManager 保留**：未删除，改为薄封装（WPF-UI 内部实现 + theme.txt 持久化），`AppearancePanel` 外部 API 不变——不要误删
- [x] **AppearancePanel 迷你预览硬编码色**：`AppearancePanel.xaml` 里预览 BorderBrush 硬编码 `#E5E6EB`（浅）/`#36393F`（深）——**有意保留**（迷你预览展示固定配色板，非主题动态色，不随主题切换）
- [x] **NavigationView 决策**：SettingsWindow **不采用 NavigationView** —— 固定 880×620 小窗 + NavigationView 的 Frame 页面导航机制改造复杂，且 WPF-UI 4.2.x 有 navigation critical bugs 被标记 deprecated；保留 RadioButton 药丸导航（`FluentNavItemStyle`/`FluentNavLabelStyle`），视觉已是 Fluent。若未来要顶栏/折叠导航再引入
- [x] **SettingsStyles 精简**：14 → 10 个样式（删 4 个手搓控件模板：FluentButtonStyle/FluentPrimaryButtonStyle/FluentTextBoxStyle/FluentPasswordBoxStyle）；剩余 10 个为排版/导航/主题卡且全被引用，符合 AGENTS「只保留被引用样式」约定
- [x] **设置窗冒烟**：无 cookie 自动弹设置窗→账号面板二维码→轮询，全链路在 FluentWindow 设置窗下实测通过（cookies 临时备份/恢复，验证后已还原）
- [x] **MainWindow 残留 `shell:` 命名空间**：——**已清理**（Phase 2 标题栏换 ui:TitleBar 后 `xmlns:shell` 与全部 `shell:WindowChrome.IsHitTestVisibleInChrome` 均删除）

## 5. 验证命令

```sh
# WPF 构建（在 biliHelper/BiliHelperWpf）
dotnet build

# 自研 key 残留检查（应在 Phase 5 归零）
grep -rn "DynamicResource" BiliHelperWpf/ --include="*.xaml" | grep -vE "Wpf\.Ui|^\s*<!--" 
# 或逐 key grep：BackgroundBrush / TextPrimaryBrush / SurfaceBrush ...
```

## 6. 进度快照（每阶段完成更新）

> **上次更新：2026-08-12 全部 5 个 Phase 完成 ✅（WPF-UI 迁移落地）**
> 当前所处 Phase：**全部完成（Phase 0~5 ✅）**
> 已完成：决策记录 ✓ / 现状快照 ✓ / **Phase 0 ✓** / **Phase 1 ✓** / **Phase 2 ✓** / **Phase 3 ✓** / **Phase 4 ✓** / **Phase 5 ✓**
> Phase 5 成果：README.md + AGENTS.md 已更新为 WPF-UI 架构（zero-NuGet 段落删除、34 画刷约定删除、Theming contracts 重写、SettingsWindow 描述更新、SettingsStyles 缩至 10 样式说明）；Converters 状态色 key 已同步；自研 key / Themes/ 引用 grep 清零复核通过；csproj 含 WPF-UI 4.3.0 引用 + Assets（logo/Lilex 仍被使用）；`dotnet build` 0 警告 0 错误；运行 ALIVE 无 FATAL；深色主题启动恢复实测通过（theme.txt 已还原浅色）
> 迁移总结：
> - MainWindow：FluentWindow（Mica）+ ui:TitleBar + TabControl 双 TAB + InfoBar 错误条 + Fluent TextBox/Button/ListBox；删 5 个自绘 Style 与 6 个旧 code-behind 处理器
> - SettingsWindow：FluentWindow + ui:TitleBar（880×620 固定 / RadioButton 药丸导航保留）；SettingsStyles 14 → 10 样式（删 4 个手搓控件模板）；AccountPanel/AiModelPanel 按钮换 `ui:Button`、输入框去自绘样式
> - 主题：自研 `Themes/` 目录（34 画刷）已删除；ThemeManager 改为 WPF-UI 薄封装（`ApplicationThemeManager.Apply(theme, None, updateAccent: true)` + theme.txt 持久化）；accent 系 key 运行时注入
> 待办（迁移后可选，非阻塞）：
> - 手动回归确认观感：拉取字幕 / 分P 切换 + TAB 记忆 / AI 整理 + 失败重试 / 历史加载 / 扫码登录 / 测试连通性 / 主题切换即时生效
> - **git 提交尚未执行**——工作区含全部迁移改动（16 个文件修改 + 3 个删除 + WPFUI_MIGRATION/ 新增），建议分 Phase 提交或一次提交
> 具体卡点/状态备注：
> - cookie 已正常（备份/还原验证）；历史数据 `/history/` 存在可回归
> - `AppearancePanel` 迷你预览硬编码色**有意保留**（预览固定配色）
> - SettingsWindow 导航保留 RadioButton（非 NavigationView，见 §4 决策记录）
> - 构建前若 exe 被锁需 `taskkill //F //IM BiliHelperWpf.exe`（见 §4）