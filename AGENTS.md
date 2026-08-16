# AGENTS.md

Bilibili subtitle extraction + desktop viewer + AI polish. **Python backend is a pure data pipeline; WPF (.NET 10) owns UI, state, and local persistence.** README.md is thorough and authoritative — read it before deep changes.

## Layout
- `BiliHelperCore/` — Python backend (`uv` project, requires-python >=3.13). No framework.
  - `main.py` (CLI, --stream JSONL), `bili_helper.py` (yt-dlp + SRT), `analyze_sub.py` (**standalone diagnostic tool, not part of the WPF pipeline**), `auth.py` (B站扫码登录/探测/用户信息, stdout JSONL), `feishu.py` (飞书云文档同步: test/sync, stdout JSONL), `AiHelper/` (`ai_read.py` per-part AI polish; `reading.py` = AIClient reading env vars)
- `BiliHelperWpf/` — .NET WPF, `net10.0-windows`, **UI 由 WPF-UI 4.3.0 提供**（`WPF-UI` + `WPF-UI.Abstractions` NuGet 包，替换原自研主题体系与手搓控件样式；zero-NuGet 约定已作废）. `Services/` spawn Python and read pipes; `ViewModels/MainViewModel.cs` is the state/concurrency hub. **自研 `Themes/` 目录已整体删除**（Light/Dark/ScrollBar 画刷），迁移到 `WPFUI_MIGRATION/` 也已完成并删除，所有主题资源由 WPF-UI 4.3.0 提供。
  - `ThemeManager.cs` = **WPF-UI 薄封装**（内部 `ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true)` + `%LocalAppData%\BiliHelper\theme.txt` 持久化；对外 API `IsDark` / `ApplyDark` / `Toggle` / `LoadPersisted` 不变，`AppearancePanel` 无需感知）. `App.xaml` 加载 `<ui:ThemesDictionary Theme="Light"/>` + `<ui:ControlsDictionary/>`；`App.xaml.cs` loads persisted theme on startup. 主窗口与设置中心均为 `FluentWindow`（`WindowBackdropType=None` + 圆角 + `TitleBar` 由库接管）——**自研 `Themes/` 目录（Light/Dark/ScrollBar，34 画刷）已整体删除**，控件颜色一律 `{DynamicResource WPF-UI key}`（如 `TextFillColorPrimaryBrush` / `CardBackgroundFillColorDefaultBrush` / `SystemFillColorCriticalBrush`）；accent 系 key 由 `ApplicationAccentColorManager` 运行时注入，`ThemeManager` 必须保持 `updateAccent: true`。**深色主题下 ThemeManager 通过 `ApplyDarkOverrides` 注入不透明背景色覆盖**（WPF-UI 默认半透明叠加在 `WindowBackdropType=None` 下会叠出死黑），浅色主题下 `ApplyLightOverrides` 淡化 ListBox 选中态。
  - `SettingsWindow.xaml(.cs)` (**设置中心**: `FluentWindow` 固定 880×620 不可拉伸 (`MinWidth=MaxWidth=880`, `MinHeight=MaxHeight=620`, `ResizeMode=NoResize`), 彻底禁用 resize 光标 (三重保险: 覆盖 `SetWindowChrome` 设 `ResizeBorderThickness=0` + `WndProc` 拦截 `WM_NCHITTEST`/`WM_SETCURSOR`), 圆角 10 + `ControlStrokeColorDefaultBrush` 外圈边框与主窗口区分; 标题栏 = WPF-UI `TitleBar` 32px 高 (从默认 48px 压缩), 带 logo 图标 12×12, **隐藏最大最小化按钮** (`ShowMaximize=ShowMinimize=False`, 只保留关闭); **RadioButton 分组导航** — 服务(获取cookie/AI模型) / 个性化(外观), 选中态为淡灰底+主色文字 (去掉左侧指示条+粗体), 无 icon (**保留自研导航, 未换 NavigationView** — 固定小窗 + NavigationView Frame 页面机制复杂且 4.2.x 有 navigation critical bugs)) + `Settings/` (`SettingsStyles.xaml` 共享样式 **10 个**: `FluentPageTitleStyle` 面板大标题统一 20px / `FluentGroupTitleStyle` 主色竖线分组头 / 分割线 / 卡片 (无阴影, 纯 border+背景) / 导航药丸 / 主题选择卡 — 按钮/输入框全部使用 WPF-UI 原生 `ui:Button Appearance` / `ui:TextBox` / `ui:PasswordBox`); `AccountPanel` 账号+扫码登录 (头像用 `EllipseGeometry` 裁剪为圆形), `AiModelPanel` AI 大模型连接 (测试/保存按钮在标题行右上角, 测试结果内联卡片), `AppearancePanel` 主题左右并排 + 迷你预览) + `Services/AiSettingsStore.cs` (持久化到 `%LocalAppData%\BiliHelper\ai_settings.json`) + `Models/AiSettings.cs`.

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
- **Must set `PYTHONIOENCODING=utf-8` on subprocess and `Encoding.UTF8` on both redirect streams.** `AuthService.BuildPsi` 曾漏设导致 stderr 中文乱码 (Python 退化为 GBK 而 C# 按 UTF-8 解码), 已修复 — **所有 spawn 点必须保持一致: BiliService×1, AuthService×1, AiReadService×2 (整理 + 连通性测试)**.
- Cancellation must kill the whole process tree (`process.Kill(entireProcessTree: true)`).

## Paths / repo-relative gotchas
- Python and cookie paths are resolved by walking up from the app base directory until a folder named **`bilihelperCore`** is found (`FindProjectRoot` / `FindDirContaining`). Works because Windows is case-insensitive; keep the relative folder layout when moving output.
- Cookies: B站登录态不再硬编码。**`auth.py` 扫码登录后写到 `%LocalAppData%\BiliHelper\cookies.json`(权威) + `cookies.txt`(Netscape, 供 yt-dlp)**. **两者均为原子写入 (`_atomic_write`: 临时文件 + `os.replace`)** — 即使进程被杀也不留半截文件. `BiliService.cs` 的 cookie 路径指向该 LocalAppData `cookies.txt`. 拔掉扫码登录就无 cookie → 需扫码. 旧文件 `www.bilibili.com_cookies.txt` 已被新流程取代. **yt-dlp 不回写正式 cookies**: `bili_helper.py::_cookies_arg` 把 cookies.txt 复制到临时副本 (mkstemp) 再传 `--cookies`, 用完删除 — yt-dlp close() 会无条件写回 --cookies 文件, 直接传正式文件会改写 auth.py 维护的文件且写失败即崩 (已踩坑). cookies.txt 不存在时不传任何 cookie 参数 (匿名抓取), **不要回退 `--cookies-from-browser`** (该参数期望浏览器名, 收到路径直接报错).
- 设备指纹: `%LocalAppData%\BiliHelper\fingerprint.json` 持久化 buvid3/buvid4 (`_attach_fingerprint` 先读本地复用, 无则拉取并原子落盘). **退出登录不删它** (设备身份, 可复用).
- 二维码: 不再落盘 `qr_login.png` — `auth.py` 内存生成 PNG → base64 (`image_base64` 字段) → stdout, WPF 解码显示. 规避多进程并发写同一图片.
- 登录并发: `AccountPanel` 用 `SemaphoreSlim _loginGate` + `_runId` 互斥串行 —「重新生成」先 `_cts.Cancel()` 并 **await 旧任务彻底结束 (含子进程 kill) 再启动新的**, 保证同一时刻只有一个 auth.py 进程 (杜绝并发写 cookies).
- 二维码过期: `qr_poll` 收到 86038 立即抛错, 不再空等 180s 超时.
- **无自动续期** — refresh 功能已整体移除（`auth.py refresh` 子命令、`check --refresh` 参数、`refresh_cookies`、RSA 公钥相关全部删除）。原因：原 `BILIBILI_PUBLIC_KEY_PEM` 是无效假公钥（`load_pem_public_key` 直接报 Invalid padding），续期从开发之初就从未成功过。cookie 约 1 月过期，过期后用户重新扫码即可（与之前实际行为一致）。`refresh_token` 仍随登录保存于 `cookies.json`（无害保留，仅供未来可能的续期实现）。
- 用户信息: `auth.py user` → GET `https://api.bilibili.com/x/web-interface/nav` → `{uname, face, mid}`. WPF `AuthService.GetUserAsync()` 读取. 仅用于展示欢迎卡片, **不持久化** (每次面板加载实时查). 注意: B 站认为这类接口为"非公开", 已因此关停知名文档仓库 — 仅自用, 勿整理传播.
- AI 设置: WPF 设置窗(⚙️) 是唯一入口, 由主窗口 URL 工具行「⚙️ 配置」按钮 (`MainWindow.xaml` `SettingsButton_Click` → `OpenSettingsWindow`) 打开 — 标题栏没有设置按钮. 写入 `%LocalAppData%\BiliHelper\ai_settings.json` (明文, 与 cookie 同目录). `AiReadService.ApplySettings` 把非空字段注入子进程环境变量 `DEEPSEEK_API_KEY/BASE_URL/MODEL`; 空字段不注入, Python 端 (`AIClient.from_env` 读 os.environ) 回退默认值, API Key 为空则报错. **没有 .env 加载逻辑** (`load_env_file` 已删除). BaseUrl 不要以 `/chat/completions` 结尾 (SDK 自动追加); 模型名不要带 provider 前缀 (如 `opencode-go/`).
- 飞书设置: `%LocalAppData%\BiliHelper\feishu_settings.json` (启用开关/AppID/Secret/群号/根文件夹, 明文同 cookie 级别, `FeishuSettingsStore`) + `feishu_sync.json` (文件夹/文档 token 映射, `feishu.py` 原子写, 防重复建夹建文档). 设置面板 = `Settings/FeishuPanel.xaml(.cs)` (导航第 3 项).
- Persistence (WPF only, no DB): `BiliHelperWpf/history/YYYYMMDD/BVxxxx/index.json` (meta + 分P 索引, 轻量) + `parts/NNN.json` (单分P 字幕, 切换分P 懒加载) + `read.json` (`parts[]`, incremental per-part, lock-protected concurrent writes). 旧单文件 raw.json 已全部迁移为新格式, 读端 (`LoadVideo`/`LoadGroups`) 保留 raw.json 兼容回退. **存储 JSON 约定: 无 BOM UTF-8 (`new UTF8Encoding(false)` 写, Python 端 `utf-8-sig` 读)、PascalCase 字段名 (read.json 例外 — 保留 snake_case JsonPropertyName)、计算属性一律 `[JsonIgnore]` 不进序列化 (已治理 SubtitleEntry/PartInfo/BiliVideoInfo/HistoryItem/ReadPartData, 防体积虚胖).**
- 存储细节: `FindVideoDir` **固定取最新日期目录** (同一 BV 重复拉取会写新日期目录, 取最新避免 read/parts 定位分裂); `SaveFromVideoInfo` 写入前 `CleanupDuplicateBvDirs` 删除该 BV 的其它日期目录 (历史列表不出现重复条目). 历史抽屉条目右上角 ✕ 删除 (`DeleteHistoryCommand` → 确认框 → `HistoryService.Delete` 递归删目录, 删的是当前加载视频时同时清空展示). 字幕搜索 **200ms 防抖 + `Task.Run` 后台过滤**, 完成时快照 (分P/关键词) 不一致即丢弃结果 — 大分P 数万条字幕防卡 UI.

## SettingsWindow UI conventions (do not break)
- 窗口属性: `FluentWindow` + `ResizeMode=NoResize` 固定 880×620 (`MinWidth=MaxWidth=880`, `MinHeight=MaxHeight=620`); 圆角 10 由 FluentWindow 的 `WindowCornerPreference=Round` 提供; 根元素 `<Border CornerRadius=10 BorderBrush=ControlStrokeColorDefaultBrush BorderThickness=1>` 提供外圈边框. **不要用透明背景 + DropShadowEffect 方案** — WindowChrome 上渲染异常 (已踩坑回滚). 不要显式设置 `WindowChrome` (FluentWindow 自带, 会双重边框).
- **彻底禁用 resize 光标**: FluentWindow 的 WindowChrome 即使 `ResizeMode=NoResize` 仍会在边缘显示 resize 光标. 解决方案三重保险 — (1) 覆盖 `SetWindowChrome()` 强制 `ResizeBorderThickness=0`; (2) `OnSourceInitialized` 注册 `WndProc` 钩子拦截 `WM_NCHITTEST`, 将 resize 区域 hit test 结果改为 `HTCLIENT`; (3) 同时拦截 `WM_SETCURSOR`, 检测到 resize hit test 时强制设置箭头光标. **不要只改 `ResizeMode` 或 `ResizeBorderThickness` — 单独都不够**.
- 标题栏 = WPF-UI `ui:TitleBar` **32px 高** (从默认 48px 压缩, 消除按钮下方空白带), 带 logo 图标 12×12 + 背景 `CardBackgroundFillColorDefaultBrush`; **设置窗隐藏最大最小化按钮** (`ShowMaximize="False"` `ShowMinimize="False"`, 只保留关闭按钮); 主窗口保留全部三个按钮. 不要改回自绘标题栏.
- 导航: `RadioButton` + `GroupName="Nav"` + `Tag=面板下标` + `Checked=Nav_Checked` (**保留自研, 未换 NavigationView** — 固定小窗 + NavigationView Frame 页面机制复杂且 WPF-UI 4.2.x 有 navigation critical bugs). **XAML 加载期 (InitializeComponent) 就会触发 Checked 事件, 此时 `_panels` 尚未初始化 — `Nav_Checked` 必须空值防御, 且 XAML 不要给默认项设 `IsChecked=True`**; 统一由构造函数 `NavigateTo(index)` 先直接赋值 `ContentHost.Content` 再同步选中态 (曾因此抛 NullReferenceException).
- **导航选中态样式**: 去掉左侧主色指示条 (Indicator), 改为淡灰底 (`SubtleFillColorSecondaryBrush`) + 主色文字 (`AccentFillColorDefaultBrush`), 正常字重 (去掉 SemiBold). 模板简化为单个 `ContentPresenter`, 无需 Grid 双列布局.
- 面板大标题用共享样式 `FluentPageTitleStyle` (统一 20px), 改字号只在它上面改; 不要覆盖到 `SettingsWindow.Resources` (会被后合并的 `SettingsStyles.xaml` 冲掉).
- `SettingsStyles.xaml` 只保留被引用的样式 (**当前 10 个**): 排版/分割线/卡片/导航药丸/主题选择卡 (FluentPageTitleStyle / FluentPageSubtitleStyle / FluentFieldLabelStyle / FluentGroupTitleStyle / FluentDividerStyle / FluentSectionDividerStyle / FluentCardStyle / FluentNavItemStyle / FluentNavLabelStyle / FluentThemeCardStyle). 手搓控件模板 (FluentButtonStyle / FluentPrimaryButtonStyle / FluentTextBoxStyle / FluentPasswordBoxStyle) **已删** — 按钮/输入框改用 WPF-UI 原生 (`ui:Button Appearance` 与 `ui:TextBox` / `ui:PasswordBox`). 零引用样式不要再加回来. **FluentCardStyle 已去掉 DropShadowEffect** — 卡片纯靠 border + 背景色区分层次, 不加阴影.
- 设置中心颜色一律 `{DynamicResource WPF-UI key}`, 随主题切换 (WPF-UI `ApplicationThemeManager` 提供主题资源).

## AI polish conventions
- One DeepSeek call per part (`response_format=json_object`), **思考模式写死关闭** (`extra_body={"thinking": {"type": "disabled"}}` in `reading.py`): 思维链 (reasoning_tokens) 是 max_tokens 消耗大头 — 思考模式下 P37 (1140 条字幕) 需 37472 tokens 被 32768 截断, 关闭后实测仅需 ~11k tokens。`max_tokens=32768` 保持不动 (余量约 3 倍, opencode 代理实测关闭思考后大分P 稳定成功); **不要调大到 65536** — opencode 代理对大请求会 500 (请求超载)。**已对比测试 `reasoning_effort=low` 档并排除**: low 虽不崩 (completion < 32768), 但段落粒度剧烈波动 (同档位一轮 83 段一轮 14 段, reasoning_tokens 一轮 155 一轮 6106), 阅读体验差 — 不要改回思考模式。classified retries in `ai_read.py`: network errors (`APIConnectionError`/`APITimeoutError`) and 5xx retry up to 2 times (2s backoff), JSON parse failure retries once, deterministic errors (auth/4xx/429/unknown) never retry; default model is hardcoded `deepseek-v4-flash` in `reading.py`.
- `ai_read.py` reads `parts/NNN.json` via `--part-file` (新契约: bv_id 从路径祖父目录推断, title 从同目录 index.json 读; `encoding="utf-8-sig"` BOM-tolerant) — 旧 `--raw raw.json --part N` 参数保留兼容. clamps subtitle indices to `1..subtitle_count`.
- Cancellation + per-part independent `CancellationTokenSource` → concurrent polish of multiple parts.
- Connectivity test: WPF 设置窗「测试连通性」→ `AiReadService.TestConnectivityAsync` → `ai_read.py --test` → `AIClient.test_connectivity()`. 失败时脚本 stdout 输出 `{"type":"test","ok":false,"message":"分类原因"}` **且退出码为 1**，C# 侧无论退出码都先解析该 JSON 行拿到分类信息；无 JSON 时才回退 stderr 的 `[ERROR]`。

## Feishu sync conventions
- 触发: AI 整理成功回调后 `MainViewModel.AutoSyncToFeishuAsync`（`SemaphoreSlim _feishuGate`(1,1) 串行, 同一时刻一个 feishu 子进程）；`FeishuSettings.IsComplete`（Enabled+三要素）不满足则静默跳过不打扰整理。状态按分P 存 `_feishuStatus/_feishuError/_feishuSyncing`, AI 润色 tab 顶部显示 + `FeishuRetryCommand` 失败重试。
- 子进程 `feishu.py`: `test`（取 token+发测试消息）/ `sync --bv-dir <视频目录> --part <N>`（stdout JSONL: status/complete/error）；凭证 env `FEISHU_APP_ID/SECRET/CHAT_ID/ROOT_FOLDER`（`FeishuService.ApplySettings` 非空注入）；设置 `%LocalAppData%\BiliHelper\feishu_settings.json`, 映射 `feishu_sync.json`（原子写, root_folder_name 变化时重置）。
- **飞书 API 实测坑点（别改回）**:
  - token: `POST /auth/v3/tenant_access_token/internal`（2h 有效, 每子进程现取）
  - 文件夹 find-or-create: `GET /drive/v1/files?folder_token=` 分页按名找 → `POST /drive/v1/files/create_folder`; 新建后 `POST /drive/v1/permissions/{token}/members?type=folder`（openchat/full_access）授权群（幂等）
  - 建文档: `POST /docx/v1/documents` `{folder_token,title}`; 写正文: `POST /docx/v1/documents/{doc}/blocks/{doc}/children`（**每批 ≤50 block**）
  - 覆盖更新: `GET .../children` 拿数量 → `DELETE /docx/v1/documents/{doc}/blocks/{doc}/children/batch_delete` `{start_index:0,end_index:count}`（按索引删且索引左移, 循环删头部）→ 重写。**没有单块 DELETE 接口**（404）; document_id 不变 → 群链接不变
  - 封面（**顺序不可反**）: ① 先建文档拿 document_id ② `POST /drive/v1/medias/upload_all`（`parent_type=docx_image`, `parent_node=document_id`, `extra={"drive_route_token":document_id}`）拿 file_token ③ `PATCH /docx/v1/documents/{doc}` `{"update_cover":{"cover":{"token":file_token}}}`。`parent_type=ccm_import_open` 是错的（403/权限不符）; medias/upload_all 需后台权限**发布版本**后才生效
  - 发消息: `POST /im/v1/messages?receive_id_type=chat_id`（msg_type=text, content=JSON 字符串）
  - 文档 URL: `https://feishu.cn/docx/{document_id}`（租户子域名需替换）
  - 标题清洗: 非法字符 `[\\/:*?"<>|\r\n\t]`→`_`, 文件夹名 ≤40、文档标题 ≤80 截断
- B站封面: `i*.hdslb.com` 直链**匿名可下（无需 cookie）**, 下载带 Referer 保险; 封面 URL **视频级**（多P thumbnail 相同）, 随 stream part 事件 `cover_url` → index.json `CoverUrl` → feishu.py 读。
- `feishu/` 目录（测试脚本+凭证）**gitignored 绝不提交**; 正式凭证经设置面板持久化到 LocalAppData。

## Performance / memory (audited, no action needed)
- **No OOM / leak risk** — audited 2026-08: child processes all use `using var` + `KillProcess(entireProcessTree)`, no Python→Python recursion (only bili_helper.py spawns yt-dlp, single level + timeout); event subscriptions are paired (AccountPanel Loaded/Unloaded); `_partTabMemory` / `_aiReadCtsMap` / `_aiReadProgress` all have `Clear()` / `finally Remove()`; stderr `Task.Run` loops break on process exit.
- **CTS not disposed is a known non-issue** — `CancellationTokenSource` never `.Dispose()`d in `MainViewModel` (`_cts` re-created per fetch, `_aiReadCtsMap` Remove without dispose). Deliberately kept: scale is bounded (~300 parts/video → hundreds of KB max), GC finalizer reclaims eventually, fixing concurrent code risks more than it saves. Do NOT "fix" this casually.

## Theming contracts (do not break)
- **自研 `Themes/` 目录 (Light/Dark/ScrollBar, 34 画刷) 已整体删除** (WPF-UI 迁移). 主题资源由 WPF-UI 4.3.0 提供: `App.xaml` 加载 `<ui:ThemesDictionary Theme="Light"/>` + `<ui:ControlsDictionary/>`. **不要重建自研画刷字典**.
- `ThemeManager.cs` = **WPF-UI 薄封装**: 内部 `ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true)`; 对外 API (`IsDark` / `ApplyDark` / `Toggle` / `LoadPersisted`) 不变; **必须保持 `updateAccent: true`** — accent 系 key (`AccentFillColorDefaultBrush` / `SystemFillColorAttentionBrush` 等) 由 `ApplicationAccentColorManager` 运行时注入, 关闭后这些 key 缺失.
- **颜色覆盖机制**: `ThemeManager` 通过 `ApplyDarkOverrides` / `ApplyLightOverrides` 在应用资源顶层注入/移除覆盖资源:
  - **深色主题**: WPF-UI 默认背景是半透明叠加, `WindowBackdropType=None` 下会叠出死黑 — 必须注入不透明浅灰覆盖 (`ApplicationBackgroundBrush` / `CardBackgroundFillColorDefaultBrush` / `ControlFillColorSecondaryBrush` 等 30+ 个 key). 同时强制 `TextOnAccentFillColorPrimaryBrush` / `AccentButtonForeground` 为白色 (WPF-UI 深色默认是黑色).
  - **浅色主题**: 淡化 ListBox 选中态 — 用 `AccentFillColorDefaultBrush` 的 6% 透明度作为 `ListBoxItemSelectedBackgroundThemeBrush`, 替代默认的实心深蓝底.
  - **关键**: 主题一致时 (`saved == IsDark`) 仍需调用 `ApplyThemeOverrides(dark)`, 否则启动时覆盖资源不会注入. 深色注入的覆盖资源必须在切回浅色时 Remove, 否则会误伤浅色主题.
- All colors must use **`DynamicResource` with WPF-UI keys** (e.g. `TextFillColorPrimaryBrush` / `CardBackgroundFillColorDefaultBrush` / `SystemFillColorCriticalBrush`) so they follow runtime theme swaps.
- Windows: both are `FluentWindow` with `WindowBackdropType=None` (主窗口已禁用 Mica 效果, 改用 `WindowBackdropType=None` 避免深色主题下半透明叠加导致的死黑背景). WindowChrome/圆角/标题栏由库接管, 不要手写 DWM 圆角或显式 `WindowChrome`. **深色主题背景色覆盖**: ThemeManager 的 `ApplyDarkOverrides` 注入不透明浅灰背景覆盖 WPF-UI 默认的半透明叠加 (`ApplicationBackgroundBrush` / `CardBackgroundFillColorDefaultBrush` 等 30+ 个 key), 切回浅色时 `ApplyLightOverrides` 移除这些覆盖.
- **`Binding.Converter` is not a dependency property** — it cannot use `DynamicResource`. Converter references (`BoolToVis`, `InverseBoolToVis`, `NullToVis`, `StatusToColor`) must stay `StaticResource` (defined in `Window.Resources`). `StatusToColorConverter` reads brushes at runtime via `Application.Current.TryFindResource` (keys: `SystemFillColorSuccessBrush` / `SystemFillColorAttentionBrush` / `SystemFillColorNeutralBrush`) so it still follows the theme.
- Theme persisted to `%LocalAppData%\BiliHelper\theme.txt`; `App.xaml.cs` calls `ThemeManager.LoadPersisted()` on startup.

## Ignored / unrelated
- `notes/` is gitignored (no such dir currently in repo). `_log/`, `history/`, `.env`, cookies are gitignored. **`feishu/` gitignored（飞书测试脚本与凭证，绝不提交）**。
- WPF 侧登录流程: `AuthService.cs` spawn 调 `auth.py`; **扫码登录已并入设置中心账号面板** `Settings/AccountPanel.xaml(.cs)`（内嵌二维码/状态/重新生成/退出, 轮询生命周期挂 Loaded/Unloaded, 复用 `MainViewModel.LoginAsync/DeleteCookiesAsync`）; 主窗口标题栏**已无** cookie 按钮 (🍪/⚠ 随扫码登录并入设置中心一并移除, 登录态入口统一在设置中心账号面板); 启动 `OnContentRendered` 探测, 无 cookie 首次自动弹设置中心. **已登录时展示欢迎卡片** (头像网络图加载失败回退👤 / 昵称 / UID / 已登录标签), 「退出登录」在标题行右上角.
- 账号面板布局约定: `StackPanel` 撑满内容区; **标题行 `Grid Width=540 HorizontalAlignment=Left` 与下方卡片右边缘对齐** — 别让标题行撑满导致按钮突出. 卡片固定 `Width=540` 靠左.
- 注意: `.env` 仅因历史遗留被 gitignore, 现在没有任何代码读取它 (`AIClient.from_env` 只读环境变量), 仓库当前也无 `.env` 文件. 若未来有人手动创建它, 可安全删除; 只有用 `uv run` 手动跑脚本时 uv 才会自动注入 `.env` 环境变量 (仅影响开发者手动执行, 不影响 WPF 直调 `.venv/python.exe` 的链路).