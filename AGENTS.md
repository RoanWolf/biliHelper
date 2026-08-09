# AGENTS.md

Bilibili subtitle extraction + desktop viewer + AI polish. **Python backend is a pure data pipeline; WPF (.NET 10) owns UI, state, and local persistence.** README.md is thorough and authoritative — read it before deep changes.

## Layout
- `BiliHelperCore/` — Python backend (`uv` project, requires-python >=3.13). No framework.
  - `main.py` (CLI, --stream JSONL), `bili_helper.py` (yt-dlp + SRT), `analyze_sub.py` (**standalone diagnostic tool, not part of the WPF pipeline**), `auth.py` (B站扫码登录/续期/探测, stdout JSONL), `AiHelper/` (`ai_read.py` per-part AI polish; `reading.py` = AIClient reading env vars)
- `BiliHelperWpf/` — .NET WPF, `net10.0-windows`, **zero NuGet packages** (pure native). `Services/` spawn Python and read pipes; `ViewModels/MainViewModel.cs` is the state/concurrency hub.
  - `Themes/` (`Light.xaml`, `Dark.xaml` = 34 same-key brushes, `ScrollBar.xaml`), `ThemeManager.cs` (runtime swap + persistence), `App.xaml.cs` loads persisted theme on startup.
  - `SettingsWindow.xaml(.cs)` (**设置中心**: 固定 880×620 不可拉伸 (`ResizeMode=NoResize`), 圆角 10 + `ButtonBorderBrush` 外圈边框与主窗口区分; 标题栏 32px / 13px 字号 / 左边距 10, 无 logo、无 "BiliHelper" 文字, 与 `MainWindow` 统一; **RadioButton 分组导航** — 服务(获取cookie/AI模型) / 个性化(外观), 主色药丸选中态, 无 icon) + `Settings/` (`SettingsStyles.xaml` 共享样式: `FluentPageTitleStyle` 面板大标题统一 20px / `FluentGroupTitleStyle` 主色竖线分组头 / 卡片 / 按钮 / 输入框 / 导航药丸 / 主题选择卡; `AccountPanel` 账号+扫码登录, `AiModelPanel` AI 大模型连接 (测试/保存按钮在标题行右上角, 测试结果内联卡片), `AppearancePanel` 主题左右并排 + 迷你预览) + `Services/AiSettingsStore.cs` (持久化到 `%LocalAppData%\BiliHelper\ai_settings.json`) + `Models/AiSettings.cs`.

## Commands
```sh
# Python deps (in BiliHelperCore)
uv sync

# Build WPF (in BiliHelperWpf)
dotnet build
```
No test project and no linter config exist — use `dotnet build` / `uv` for verification.

## Architecture — do not break these contracts
- Cross-process transport is **stdout JSONL** (one JSON object per line) from Python to WPF. Python must `print(json.dumps(...), flush=True)`. WPF reads each line and dispatches via `StreamEvent` / `AiReadMeta`.
- All three Python spawners (`BiliService.cs`, `AiReadService.cs`, `AuthService.cs`) launch a subprocess at **`BiliHelperCore/.venv/Scripts/python.exe` directly — never via `uv run`** (avoids uv env discovery; consistent with how `bili_helper.py` invokes yt-dlp).
- **Must set `PYTHONIOENCODING=utf-8` on subprocess and `Encoding.UTF8` on both redirect streams.** `AuthService.BuildPsi` 曾漏设导致 stderr 中文乱码 (Python 退化为 GBK 而 C# 按 UTF-8 解码), 已修复 — 三处必须保持一致.
- Cancellation must kill the whole process tree (`process.Kill(entireProcessTree: true)`).

## Paths / repo-relative gotchas
- Python and cookie paths are resolved by walking up from the app base directory until a folder named **`bilihelperCore`** is found (`FindProjectRoot` / `FindDirContaining`). Works because Windows is case-insensitive; keep the relative folder layout when moving output.
- Cookies: B站登录态不再硬编码。**`auth.py` 扫码登录后写到 `%LocalAppData%\BiliHelper\cookies.json`(权威) + `cookies.txt`(Netscape, 供 yt-dlp)**. **两者均为原子写入 (`_atomic_write`: 临时文件 + `os.replace`)** — 即使进程被杀也不留半截文件. `BiliService.cs` 的 cookie 路径指向该 LocalAppData `cookies.txt`. 拔掉扫码登录就无 cookie → 需扫码. 旧文件 `www.bilibili.com_cookies.txt` 已被新流程取代.
- 设备指纹: `%LocalAppData%\BiliHelper\fingerprint.json` 持久化 buvid3/buvid4 (`_attach_fingerprint` 先读本地复用, 无则拉取并原子落盘). **退出登录不删它** (设备身份, 可复用).
- 二维码: 不再落盘 `qr_login.png` — `auth.py` 内存生成 PNG → base64 (`image_base64` 字段) → stdout, WPF 解码显示. 规避多进程并发写同一图片.
- 登录并发: `AccountPanel` 用 `SemaphoreSlim _loginGate` + `_runId` 互斥串行 —「重新生成」先 `_cts.Cancel()` 并 **await 旧任务彻底结束 (含子进程 kill) 再启动新的**, 保证同一时刻只有一个 auth.py 进程 (杜绝并发写 cookies).
- 二维码过期: `qr_poll` 收到 86038 立即抛错, 不再空等 180s 超时.
- B站续期逻辑在 `auth.py` 内（`auth.py refresh` / `auth.py check --refresh`），无需独立脚本；cookie 约 1 月过期，WPF 启动时自动续期. **`confirm/refresh` 必须用本次下发的新 refresh_token 确认** (响应未给时回退旧的, 绝不存空串) — 曾用旧 token 导致刷新不被 B 站认可.
- 用户信息: `auth.py user` → GET `https://api.bilibili.com/x/web-interface/nav` → `{uname, face, mid}`. WPF `AuthService.GetUserAsync()` 读取. 仅用于展示欢迎卡片, **不持久化** (每次面板加载实时查). 注意: B 站认为这类接口为"非公开", 已因此关停知名文档仓库 — 仅自用, 勿整理传播.
- AI 设置: WPF 设置窗(⚙️) 是唯一入口, 由主窗口 URL 工具行「⚙️ 配置」按钮 (`MainWindow.xaml` `SettingsButton_Click` → `OpenSettingsWindow`) 打开 — 标题栏没有设置按钮. 写入 `%LocalAppData%\BiliHelper\ai_settings.json` (明文, 与 cookie 同目录). `AiReadService.ApplySettings` 把非空字段注入子进程环境变量 `DEEPSEEK_API_KEY/BASE_URL/MODEL`; 空字段不注入, Python 端 (`AIClient.from_env` 读 os.environ) 回退默认值, API Key 为空则报错. **没有 .env 加载逻辑** (`load_env_file` 已删除). BaseUrl 不要以 `/chat/completions` 结尾 (SDK 自动追加); 模型名不要带 provider 前缀 (如 `opencode-go/`).
- Persistence (WPF only, no DB): `BiliHelperWpf/history/YYYYMMDD/BVxxxx/raw.json` (full video) and `read.json` (`parts[]`, incremental per-part, lock-protected concurrent writes).

## SettingsWindow UI conventions (do not break)
- 窗口属性: `ResizeMode=NoResize` 固定大小; `WindowChrome.CornerRadius=10`; 根元素 `<Border CornerRadius=10 BorderBrush=ButtonBorderBrush BorderThickness=1>` 提供外圈边框. **不要用透明背景 + DropShadowEffect 方案** — WindowChrome 上渲染异常 (已踩坑回滚).
- 标题栏与主窗口统一: 32px 高 / 13px 字号 / 左边距 10; 无 logo、无 "BiliHelper" 文字.
- 导航: `RadioButton` + `GroupName="Nav"` + `Tag=面板下标` + `Checked=Nav_Checked`. **XAML 加载期 (InitializeComponent) 就会触发 Checked 事件, 此时 `_panels` 尚未初始化 — `Nav_Checked` 必须空值防御, 且 XAML 不要给默认项设 `IsChecked=True`**; 统一由构造函数 `NavigateTo(index)` 先直接赋值 `ContentHost.Content` 再同步选中态 (曾因此抛 NullReferenceException).
- 面板大标题用共享样式 `FluentPageTitleStyle` (统一 20px), 改字号只在它上面改; 不要覆盖到 `SettingsWindow.Resources` (会被后合并的 `SettingsStyles.xaml` 冲掉).
- `SettingsStyles.xaml` 只保留被引用的样式 (当前 14 个); 零引用样式 (`FluentHintTextStyle` / `FluentSectionTitleStyle` / `FluentCardTitleStyle` / `FluentSubCardStyle` / `FluentTextButtonStyle` / 主题 `SuccessBackgroundBrush`) 已删, 不要再加回来.
- 设置中心颜色一律 `DynamicResource`, 随主题切换; 两套主题仍保持 34 same-key brushes.

## AI polish conventions
- One DeepSeek call per part (`response_format=json_object`), parse failure retries once; default model is hardcoded `deepseek-v4-flash` in `reading.py`.
- `ai_read.py` reads `raw.json` with `encoding="utf-8-sig"` (BOM-tolerant) and clamps subtitle indices to `1..subtitle_count`.
- Cancellation + per-part independent `CancellationTokenSource` → concurrent polish of multiple parts.
- Connectivity test: WPF 设置窗「测试连通性」→ `AiReadService.TestConnectivityAsync` → `ai_read.py --test` → `AIClient.test_connectivity()`. 失败时脚本 stdout 输出 `{"type":"test","ok":false,"message":"分类原因"}` **且退出码为 1**，C# 侧无论退出码都先解析该 JSON 行拿到分类信息；无 JSON 时才回退 stderr 的 `[ERROR]`。

## Theming contracts (do not break)
- `Light.xaml` / `Dark.xaml` must define **the same brush keys** (34 each) — swapping the dictionary refreshes all `DynamicResource`. `ThemeManager` locates the theme dictionary by regex `Light\.xaml$|Dark\.xaml$` (never the shared `ScrollBar.xaml`).
- MainWindow colors must use **`DynamicResource`** so they follow runtime theme swaps.
- **`Binding.Converter` is not a dependency property** — it cannot use `DynamicResource`. Converter references (`BoolToVis`, `InverseBoolToVis`, `NullToVis`, `StatusToColor`) must stay `StaticResource` (defined in `Window.Resources`). `StatusToColorConverter` reads brushes at runtime via `Application.Current.TryFindResource` so it still follows the theme.
- In `ScrollBar.xaml`, any referenced `x:Key` style must be declared **before** the implicit style (`StaticResource` resolves forward only).
- Theme persisted to `%LocalAppData%\BiliHelper\theme.txt`; `App.xaml.cs` calls `ThemeManager.LoadPersisted()` on startup.

## Ignored / unrelated
- `notes/` contains personal OS/study notes — not part of the codebase. `_log/`, `history/`, `.env`, cookies are gitignored.
- WPF 侧登录流程: `AuthService.cs` spawn 调 `auth.py`; **扫码登录已并入设置中心账号面板** `Settings/AccountPanel.xaml(.cs)`（内嵌二维码/状态/重新生成/退出, 轮询生命周期挂 Loaded/Unloaded, 复用 `MainViewModel.LoginAsync/DeleteCookiesAsync`）; 主窗口标题栏**已无** cookie 按钮 (🍪/⚠ 随扫码登录并入设置中心一并移除, 登录态入口统一在设置中心账号面板); 启动 `OnContentRendered` 探测, 无 cookie 首次自动弹设置中心. **已登录时展示欢迎卡片** (头像网络图加载失败回退👤 / 昵称 / UID / 已登录标签), 「退出登录」在标题行右上角.
- 账号面板布局约定: `StackPanel` 撑满内容区; **标题行 `Grid Width=540 HorizontalAlignment=Left` 与下方卡片右边缘对齐** — 别让标题行撑满导致按钮突出. 卡片固定 `Width=540` 靠左.
- 注意: `.env` 仅因历史遗留被 gitignore, 现在没有任何代码读取它 (`AIClient.from_env` 只读环境变量). 本地残留的 `.env` 文件可安全删除; 若有人用 `uv run` 手动跑脚本, uv 会自动注入 `.env` 环境变量 (仅影响开发者手动执行, 不影响 WPF 直调 `.venv/python.exe` 的链路).