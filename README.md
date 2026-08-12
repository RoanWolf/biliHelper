# BiliHelper

B 站字幕提取 + 桌面查看器 + AI 润色。Python 后端负责字幕抓取与 AI 润色，WPF 桌面端负责展示、状态管理和本地历史存储。

---

## 项目结构

```
biliHelper/
├── BiliHelperCore/                ← Python 后端（纯数据管道）
│   ├── main.py                    CLI 入口（字幕抓取）
│   ├── bili_helper.py             字幕提取核心逻辑（yt-dlp + SRT 解析）
│   ├── auth.py                    B 站扫码登录 / 探测 / 用户信息（供 WPF 子进程调用，二维码走 base64 不落盘、指纹持久化、cookies 原子写）
│   ├── analyze_sub.py             字幕分析工具
│   ├── AiHelper/                   AI 模块（DeepSeek）
│   │   ├── reading.py             AIClient 封装 + .env 加载
│   │   └── ai_read.py             单分P AI 润色脚本（供 WPF 子进程调用）
│   ├── pyproject.toml             uv 项目配置（依赖 openai + yt-dlp + requests + qrcode）
│   ├── uv.lock                    依赖锁
│   └── .venv/                     虚拟环境
│
├── BiliHelperWpf/                 ← WPF 桌面端（.NET 10）
│   ├── Models/                    数据模型
│   │   ├── AuthEvent.cs           登录事件 + CookieState 枚举
│   │   ├── AiSettings.cs          AI 大模型连接设置（API key / base_url / model）
│   │   ├── BiliVideoInfo.cs       顶层视频信息
│   │   ├── PartInfo.cs            分P 信息
│   │   ├── SubtitleEntry.cs       单条字幕
│   │   ├── Paragraph.cs           AI 润色段落
│   │   ├── ReadPartData.cs        单分P 润色数据
│   │   ├── HistoryItem.cs         历史记录条目
│   │   ├── HistoryGroup.cs        历史记录日期分组
│   │   └── StreamEvent.cs         流式事件模型
│   ├── Services/
│   │   ├── BiliService.cs         字幕抓取子进程管理 + 管道读取
│   │   ├── AiReadService.cs       AI 润色子进程管理 + 管道读取
│   │   ├── AiSettingsStore.cs     AI 设置持久化（%LocalAppData%\BiliHelper\ai_settings.json）
│   │   ├── AuthService.cs         B 站登录子进程管理（auth.py）+ 管道读取
│   │   └── HistoryService.cs      本地 JSON 文件存储（raw/read）
│   ├── ViewModels/
│   │   ├── MainViewModel.cs       主视图模型（状态管理、并发控制、cookie 状态）
│   │   ├── RelayCommand.cs        ICommand 实现
│   │   └── ViewModelBase.cs       INotifyPropertyChanged 基类
│   ├── Converters/
│   │   ├── BoolToVisibilityConverter.cs
│   │   ├── NullToVisibilityConverter.cs
│   │   └── StatusToColorConverter.cs
│   ├── ThemeManager.cs             主题切换薄封装（内部调用 WPF-UI ApplicationThemeManager；持久化 %LocalAppData%\BiliHelper\theme.txt）
│   ├── SettingsWindow.xaml(.cs)    设置中心（分组导航：服务/个性化，RadioButton 选中态淡灰底+主色文字；FluentWindow 固定大小 880×620、禁用 resize 光标三重保险、隐藏最大最小化按钮）
│   ├── Settings/
│   │   ├── SettingsStyles.xaml     共享样式（10 个：页面标题/分组标题/卡片/导航药丸/主题选择卡，FontSize 由顶部统一控制）
│   │   ├── AccountPanel.xaml(.cs)  账号面板：内嵌扫码登录（互斥串行防并发写）+ 欢迎卡片（头像/昵称/UID）+ 退出
│   │   ├── AiModelPanel.xaml(.cs)  AI 大模型连接面板（API Key / Base URL / 模型，测试/保存按钮在标题行右上角，测试结果内联卡片）
│   │   └── AppearancePanel.xaml(.cs) 主题选择面板（左右并排 + 迷你预览 + 右上角单选圈）
│   ├── MainWindow.xaml             主界面（FluentWindow + WPF-UI；标题栏无 cookie/设置按钮；URL 工具行含「⚙️ 配置」入口，颜色走 DynamicResource）
│   ├── MainWindow.xaml.cs          窗口隐藏、设置中心开关（DWM 圆角由 FluentWindow 接管）
│   ├── WpfHelper.cs                可视化树工具方法
│   └── history/                    本地历史记录（用户数据，gitignore）
│
├── _log/                           运行时日志（gitignore）
├── .gitignore
└── README.md                        ← 本文档
```

---

## 架构：管道流式传输

### 核心理念

**Python 是一个纯数据管道，不负责持久化、不负责 UI、不负责缓存。**
**WPF 负责展示、状态管理、本地存储。**

### 调用链路（字幕抓取）

```
用户点击「拉取字幕」
    │
    ▼
MainViewModel.FetchAsync(url)
    │
    ▼
BiliService.FetchStreamAsync(url, onMeta, onPart, onComplete, ...)
    │
    ├── Process.Start(python.exe, main.py, url, -c, cookies.txt, --stream)
    │   │
    │   │   ╔══════════════════════════════════════════════╗
    │   │   ║         子进程 (Python)                       ║
    │   │   ║                                              ║
    │   │   ║  main.py --stream  →  get_subtitles_stream() ║
    │   │   ║                                              ║
    │   │   ║  for item in generator:                      ║
    │   │   ║      print(json.dumps(item), flush=True)     ║
    │   │   ║      → stdout                               ║
    │   │   ╚══════════════════════════════════════════════╝
    │   │
    │   │  stdout 管道（WPF 逐行读取）
    │   ▼
    │  while (line = await process.StandardOutput.ReadLineAsync()) != null:
    │      ParseLine(line)  →  StreamEvent
    │      switch ev.Type:
    │          Meta     → onMeta(ev)
    │          Part     → onPart(ev)
    │          Complete → onComplete(ev)
    │
    ▼
    ui.Invoke() → 更新 UI
```

### 调用链路（AI 润色）

```
用户选中分P → 点击「整理当前分P」（AI润色 TAB）
    │
    ▼
MainViewModel.GenerateReadAsync()
    │   每个分P 独立 CancellationTokenSource，支持多分P 并发整理
    ▼
AiReadService.GenerateReadDataAsync(bvId, partNumber, ...)
    │
    ├── Process.Start(python.exe, ai_read.py, --raw, raw.json, --part, N)
    │   │   启动前 ApplySettings()：把设置窗非空字段注入环境变量
    │   │
    │   │   ╔══════════════════════════════════════════════╗
    │   │   ║         子进程 (Python)                       ║
    │   │   ║                                              ║
    │   │   ║  ai_read.py  →  读取 raw.json 单分P          ║
    │   │   ║             →  调 DeepSeek（一次，不分块）   ║
    │   │   ║             →  stdout: {type:meta} {type:complete} ║
    │   │   ║             →  stderr: 进度文本              ║
    │   │   ╚══════════════════════════════════════════════╝
    │   │
    │   ▼
    ├─ 解析段落 → 写 read.json（加锁，并发安全）
    │   └─ 若当前选中分P 是发起者 → ui.Invoke() 刷新段落展示
    │
    ▼
    HistoryService.SaveReadPart() → read.json（增量，按分P）
```

### 传输方式

| 概念 | 实现 |
|------|------|
| **传输介质** | 操作系统管道（pipe），非 HTTP、非文件 |
| **格式** | JSONL（每行一个完整 JSON） |
| **实时性** | Python 端 `flush=True` 确保每行立即推送 |
| **WPF 读取** | 两个 `Task.Run`：stdout（数据）、stderr（进度） |
| **进程关系** | WPF 父进程创建 Python 子进程，子进程退出后管道关闭 |
| **Python 调用方式** | 直接调 `.venv/Scripts/python.exe`（绕开 uv run 的环境发现，与 bili_helper.py 调 yt-dlp 一致） |

---

## 本地存储

WPF 负责，Python 不参与。

```
BiliHelperWpf/history/
├── 20260730/                    ← 日期文件夹（YYYYMMDD，即索引）
│   └── BV1f7366GEBe/            ← BV 号目录（天然唯一）
│       ├── raw.json             ← 原始字幕（含 meta 索引 + data 完整数据）
│       └── read.json            ← AI 润色结果（按分P 增量）
└── ...
```

- **raw.json**：视频完整数据（meta 索引信息 + data 全部字幕），拉取完成时写入
- **read.json**：AI 润色结果，`parts[]` 数组，只包含整理过的分P；整理哪个分P 就增量写入哪条，支持并发写（有锁保护）
- 文件夹即索引，无数据库、无额外索引文件

---

## B 站登录（扫码）

字幕 / 历史 CSV 导出等需要 B 站登录态，登录态走**扫码登录**（不再硬编码 cookie）。

### 存储位置（每台机器一份，非 git 追踪）

```
%LocalAppData%\BiliHelper\
├── cookies.json    权威存储 {cookies, refresh_token, ...}（原子写入，绝不半截）
├── cookies.txt     给 yt-dlp 用的 Netscape 格式（BiliService 读取，原子写入）
├── fingerprint.json 设备指纹 buvid3/buvid4（跨进程复用，降低风控）
├── theme.txt       主题偏好
└── ai_settings.json AI 大模型连接设置（API key / base_url / model）
```

### 调用链路（扫码登录）

```
WPF 启动 → OnContentRendered → auth.py check   ← 自动探测
                                                  └─ 无 cookie → 自动弹设置中心账号面板
    │
    ▼
AccountPanel.StartLoginAsync()   （内嵌于设置中心，轮询挂 Loaded/Unloaded）
    │   并发安全：登录流程互斥串行（SemaphoreSlim）——「重新生成」先取消上一轮并
    │   await 旧任务彻底结束（含子进程 kill）再启动新的，保证同一时刻只有一个 auth.py
    │   子进程存活，杜绝并发写 cookies 文件。
    ▼
MainViewModel.LoginAsync()
    │
    ▼
AuthService.LoginAsync(onQr, onStatus, onError, ...)
    │
    ├── Process.Start(python.exe, auth.py login)
    │   │   ① 复用本地 fingerprint.json 设备指纹；无则调 finger/spi 拉取并落盘
    │   │   ② qrcode/generate 生成 url + qrcode_key → 内存生成 PNG → base64
    │   │         （不再落盘 qr_login.png，避免并发写图竞争）
    │   │   ③ 每 1.5s 轮询 qrcode/poll，状态码：
    │   │        86101 等待扫码 / 86090 已扫码 / 0 成功 / 86038 立即过期报错
    │   │   成功 → 合并 Set-Cookie + URL query 提取真实凭证
    │   │         (SESSDATA/bili_jct/DedeUserID...) → 原子写 cookies.json/.txt
    │   │   完成 → stdout: {type:qr/status/success/error}  JSONL（qr 带 image_base64）
    │   ▼
  读 stdout → AccountPanel 从 base64 解码显示二维码 / 更新状态 / 成功后刷新 cookie 状态
    │
    ▼
AccountPanel.LoadUserInfoAsync()   （已登录时展示欢迎卡片）
    │   └─ auth.py user → GET x/web-interface/nav → {uname, face, mid}
    │         → 头像(网络图，失败回退👤) + 昵称 + UID 欢迎卡片
```

### 交互细节

- 主窗口标题栏**已无** cookie 按钮（🍪/⚠ 随扫码登录并入设置中心一并移除）——登录态入口统一在设置中心账号面板；设置中心由 **URL 工具行「⚙️ 配置」按钮** 打开（非标题栏）
- 账号面板标题行右上角「退出登录」→ 删除本地 cookie，状态置为未登录（**不删** fingerprint.json，设备指纹可复用）
- 已登录时展示**欢迎卡片**：头像（网络图，加载失败回退 👤 占位）+ 昵称 + UID + 已登录标签
- 「重新生成」在二维码显示后即可用（轮询中也能点）：先取消并等旧轮次彻底结束再开新轮次，无并发
- 启动 `OnContentRendered` 探测 cookie，无 cookie 时首次自动弹出设置中心账号面板

### 已知注意事项

- **B 站对高频连续扫码有风控**：短时间连续 generate/扫码时，新 key 可能不被确认（一直 86101）。单次等待 + 扫码即可正常通过，属 B 站侧限流而非代码缺陷。
- cookie 约 1 月过期，**无自动续期**（refresh 功能已移除：原 RSA 公钥无效、续期从未成功，属不可用功能，已删除 `refresh` / `check --refresh`）——过期后重新扫码即可；`cookies.json/.txt` 均为原子写入，进程被杀也不留半截文件。

---

## AI 润色（OpenAI 兼容接口）

### 配置

唯一入口是 **WPF 设置中心**（URL 工具行「⚙️ 配置」按钮打开，`SettingsWindow` → `Settings/AiModelPanel`）：填 API Key / Base URL / 模型，点「测试连通性」验证后保存。持久化到 `%LocalAppData%\BiliHelper\ai_settings.json`（明文，与 cookie 同一目录同一安全级别）。

**注入机制**：WPF 启动 Python 子进程时，把设置窗中**非空**的字段通过环境变量 `DEEPSEEK_API_KEY` / `DEEPSEEK_BASE_URL` / `DEEPSEEK_MODEL` 注入；空字段不注入，Python 端回退到默认值（BaseUrl 默认 `https://api.deepseek.com`，模型默认 `deepseek-v4-flash`）。API Key 为空则明确报错并引导去设置窗填写。

**注意事项**：
- `Base URL` 填 SDK 的 base_url，**不要**以 `/chat/completions` 结尾（OpenAI SDK 会自动追加）；例如 OpenCode Zen 填 `https://opencode.ai/zen/go/v1`。
- 模型名**不要**带 provider 前缀（如 `opencode-go/...`），填纯模型 ID 如 `deepseek-v4-flash`。
- 「测试连通性」调用 `ai_read.py --test`，Python 端按认证失败 / 模型不存在 / 限流 / 网络不通分类报错。

### 核心逻辑

- 手动触发：当前分P 未整理时，AI润色 TAB 显示空状态卡片，点击按钮启动整理
- **每分P 一次调用**，不分块（单分P 最大约 5000 token，上下文充足）
- **支持多分P 并发整理**：每个分P 独立子进程 + CancellationToken，互不干扰
- AI 返回 JSON 的 `response_format` 强制，分类重试：网络错误（断连/超时）与 5xx 重试最多 2 次（间隔 2s），JSON 解析失败重试 1 次，认证/4xx/限流等确定性错误不重试
- **思考模式写死关闭**（`reading.py` 的 `extra_body={"thinking": {"type": "disabled"}}`）：思维链（reasoning_tokens）是 max_tokens 消耗大头——思考模式下 P37（1140 条字幕）需 37472 tokens，被 `max_tokens=32768` 截断；关闭后实测仅需 ~11k tokens，32768 余量约 3 倍（opencode 代理实测关闭思考后大分P 稳定成功）。**不要调大到 65536**——opencode 代理对大请求会 500（请求超载）。**已对比测试 `reasoning_effort=low` 档并排除**：low 虽不崩（completion < 32768），但段落粒度剧烈波动（同档位一轮 83 段一轮 14 段，reasoning_tokens 一轮 155 一轮 6106），阅读体验差——不要改回思考模式
- 已整理的分P 显示段落列表，按钮禁用；切换分P 实时加载对应 read.json 数据

### 交互细节

- 「原始字幕」与「AI润色」两个 TAB 按分P 记忆——每个分P 记住自己最后使用的 TAB，切走再切回能还原
- 整理中按钮隐藏、显示进度文本；切走分P 不打断后台整理，切回可见结果
- 整理失败的错误提示显示在 TAB 内容区顶部，可重试

---

## 多主题（浅 / 深色）

WPF 端支持浅色 / 深色两种主题，运行时可在设置中心「外观」面板切换（标题栏 ☀️/🌙 按钮已随扫码登录合并移除），选择持久化到 `%LocalAppData%\BiliHelper\theme.txt`，下次启动自动恢复。

### 结构（WPF-UI 接管，自研主题已移除）

- 主题资源由 **WPF-UI 4.3.0** 提供：`App.xaml` 加载 `<ui:ThemesDictionary Theme="Light"/>` + `<ui:ControlsDictionary/>`；切换时 `ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: true)` 替换主题字典并同步注入 accent 颜色资源。
- `ThemeManager.cs`：**薄封装**——对外 API（`IsDark` / `ApplyDark` / `Toggle` / `LoadPersisted`）不变，内部调用 WPF-UI；持久化仍写入 `%LocalAppData%\BiliHelper\theme.txt`。
- 自研 `Themes/` 目录（Light/Dark/ScrollBar，34 画刷）与 `ThemeManager` 的字典替换逻辑**已整体移除**；控件指针仅保留 `{DynamicResource WPF-UI key}`（如 `TextFillColorPrimaryBrush` / `CardBackgroundFillColorDefaultBrush` / `SystemFillColorCriticalBrush`）。
- 窗口框架：主窗口与设置中心均为 `FluentWindow`（`WindowBackdropType=None` / 圆角 / 原生标题栏由库接管）。深色主题下 WPF-UI 默认背景是半透明叠加，在 `WindowBackdropType=None` 下会叠出死黑 — `ThemeManager` 通过 `ApplyDarkOverrides` 注入不透明浅灰覆盖（`ApplicationBackgroundBrush` / `CardBackgroundFillColorDefaultBrush` 等 30+ 个 key），浅色主题下 `ApplyLightOverrides` 移除这些覆盖。

### 关键约定（勿破坏）

- **主界面颜色一律用 `DynamicResource`**（主题切换时即时刷新）。
- `Binding.Converter` 属性不是依赖属性，**不能**用 `DynamicResource`，因此 `BoolToVis` / `InverseBoolToVis` / `NullToVis` / `StatusToColor` 这类 converter 引用必须用 `StaticResource`（定义在 `Window.Resources` 内）。
- 事件色转换器（`StatusToColorConverter`）通过 `Application.Current.TryFindResource` 运行时取色（key 已改为 WPF-UI 的 `SystemFillColorSuccessBrush` / `SystemFillColorAttentionBrush` / `SystemFillColorNeutralBrush`），因此能随主题变化。
- **accent 系资源（`AccentFillColorDefaultBrush` / `SystemFillColorAttentionBrush` 等）由 `ApplicationAccentColorManager` 在调用 `Apply(..., updateAccent: true)` 时运行时注入**——`ThemeManager` 必须保持 `updateAccent: true`，否则这些 key 缺失。
- **颜色覆盖机制**：`ThemeManager` 通过 `ApplyDarkOverrides` / `ApplyLightOverrides` 在应用资源顶层注入/移除覆盖资源。深色主题注入不透明浅灰覆盖（避免死黑），浅色主题淡化 ListBox 选中态。主题一致时仍需调用 `ApplyThemeOverrides(dark)`，否则启动时覆盖资源不会注入。

---

## 设置中心

WPF 设置中心（⚙️ `SettingsWindow`）是唯一的配置入口：账号扫码登录、AI 大模型连接、外观主题。Fluent 风格，**固定大小**（880×620，`ResizeMode=NoResize`）、圆角 + 外圈边框，与主窗口在视觉上区分。

### 布局

- `FluentWindow` 固定 880×620（`MinWidth=MaxWidth=880`, `MinHeight=MaxHeight=620`, `ResizeMode=NoResize`），圆角 + 外圈边框与主窗口区分。
- **彻底禁用 resize 光标**：FluentWindow 的 WindowChrome 即使 `ResizeMode=NoResize` 仍会在边缘显示 resize 光标 — 三重保险：(1) 覆盖 `SetWindowChrome()` 强制 `ResizeBorderThickness=0`；(2) `WndProc` 拦截 `WM_NCHITTEST`，将 resize 区域改为 `HTCLIENT`；(3) 同时拦截 `WM_SETCURSOR`，检测到 resize hit test 时强制设置箭头光标。
- 标题栏为 WPF-UI `TitleBar`（**32px** 高，从默认 48px 压缩消除按钮下方空白带），带 logo 图标 12×12 + 背景 `CardBackgroundFillColorDefaultBrush`；**隐藏最大最小化按钮**（`ShowMaximize="False"` `ShowMinimize="False"`，只保留关闭按钮）。
- 左侧**分组导航**（`RadioButton`，选中态为**淡灰底 + 主色文字**，去掉左侧指示条和粗体，模板简化为单个 ContentPresenter，无 icon）：
  - **服务**：获取cookie、AI模型
  - **个性化**：外观
- 右侧内容区承载面板；`Settings/SettingsStyles.xaml` 共享样式（页面标题 `FluentPageTitleStyle`、分组标题 `FluentGroupTitleStyle`、卡片/按钮/输入框/导航药丸/主题卡），面板大标题字号统一 20px。

### 面板

| 面板 | 文件 | 说明 |
|------|------|------|
| 账号 | `Settings/AccountPanel.xaml(.cs)` | 内嵌扫码登录（二维码/状态/重新生成/退出），轮询挂 Loaded/Unloaded；复用 `MainViewModel.LoginAsync/DeleteCookiesAsync` |
| AI 模型 | `Settings/AiModelPanel.xaml(.cs)` | API Key / Base URL / 模型；「测试连通性」「保存」在标题行右上角，测试结果内联卡片显示在连接配置卡下方 |
| 外观 | `Settings/AppearancePanel.xaml(.cs)` | 浅色/深色**左右并排**主题卡，各带 72px 迷你预览，单选圈悬浮右上角 |

### 交互细节

- 无 cookie 时启动 `OnContentRendered` 自动弹出设置中心并定位到账号面板。
- 主窗口「设置」按钮复用同一窗口实例：已打开则 `NavigateTo(index)` + Activate，避免重复窗口。
- **导航切换用 RadioButton + Tag 下标**：`NavigateTo` 直接赋值 `ContentHost.Content`（默认选中项不触发 `Checked` 事件），`Nav_Checked` 对 `_panels` 做空值防御（XAML 加载期事件早于字段初始化）。

---

## 日志

- `_log/BiliHelperWpf_Log.txt`，每次启动覆盖，记录完整数据流：
  - 字幕抓取：启动、meta、每分P、完成
  - AI 润色：开始、子进程启动、stdout/stderr、完成回调、read.json 保存、取消/异常
  - B 站登录：check 探测、扫码轮询状态、设备指纹、结果
- 适合排查问题时开启，测试完成后可移除细粒度日志

---

## 依赖

| 层级 | 技术 | 版本 |
|------|------|------|
| 后端运行时 | Python | ≥ 3.13 |
| 包管理 | uv | — |
| 字幕下载 | yt-dlp | ≥ 2024.1 |
| AI 调用 | openai (DeepSeek 兼容接口) | ≥ 2.53.0 |
| B 站登录 | requests + qrcode + pillow | — |
| 桌面框架 | .NET WPF | net10.0-windows |
| UI 库 | WPF-UI | 4.3.0（含 WPF-UI.Abstractions；替换原自研主题与控件样式） |

---

## 环境搭建

```shell
# Python 依赖
cd bilihelperCore
uv sync

# WPF 构建（项目根目录）
cd BiliHelperWpf
dotnet build
```

首次使用：运行应用后点击 URL 工具行的「⚙️ 配置」打开设置中心，在「AI 大模型」面板填写 API Key / Base URL / 模型，点「测试连通性」验证并保存；若未登录 B 站，设置中心会首次自动弹出并定位到「账号」面板扫码登录。
