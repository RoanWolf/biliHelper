# BiliHelper

B 站字幕提取 + 桌面查看器 + AI 润色。Python 后端负责字幕抓取与 AI 润色，WPF 桌面端负责展示、状态管理和本地历史存储。

---

## 项目结构

```
biliHelper/
├── bilihelperCore/               ← Python 后端（纯数据管道）
│   ├── main.py                    CLI 入口（字幕抓取）
│   ├── bili_helper.py             字幕提取核心逻辑（yt-dlp + SRT 解析）
│   ├── analyze_sub.py             字幕分析工具
│   ├── AiHelper/                  AI 模块（DeepSeek）
│   │   ├── reading.py             AIClient 封装 + .env 加载
│   │   └── ai_read.py             单分P AI 润色脚本（供 WPF 子进程调用）
│   ├── pyproject.toml             uv 项目配置（依赖 openai + yt-dlp）
│   ├── uv.lock                    依赖锁
│   ├── .venv/                     虚拟环境（含 yt-dlp、openai）
│   ├── .env                       DeepSeek API key（已被 .gitignore 忽略）
│   ├── .env.example               .env 模板（可提交）
│   └── www.bilibili.com_cookies.txt  B 站登录态
│
├── BiliHelperWpf/                 ← WPF 桌面端（.NET 10）
│   ├── Models/                    数据模型
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
│   │   └── HistoryService.cs      本地 JSON 文件存储（raw/read）
│   ├── ViewModels/
│   │   ├── MainViewModel.cs       主视图模型（状态管理、并发控制）
│   │   ├── RelayCommand.cs        ICommand 实现
│   │   └── ViewModelBase.cs       INotifyPropertyChanged 基类
│   ├── Converters/
│   │   ├── BoolToVisibilityConverter.cs
│   │   ├── NullToVisibilityConverter.cs
│   │   └── StatusToColorConverter.cs
│   ├── Themes/                    多主题（浅 / 深色）
│   │   ├── Light.xaml             浅色主题（34 个画刷，运行时可切换）
│   │   ├── Dark.xaml              深色主题（与 Light 同 key）
│   │   └── ScrollBar.xaml         通用细滚动条（颜色随主题）
│   ├── ThemeManager.cs            主题切换 / 持久化（%LocalAppData%\BiliHelper\theme.txt）
│   ├── MainWindow.xaml            主界面（颜色全部走 DynamicResource）
│   ├── MainWindow.xaml.cs         代码隐藏（含 DWM 圆角、主题切换按钮）
│   ├── WpfHelper.cs               可视化树工具方法
│   └── history/                   本地历史记录（用户数据，gitignore）
│
├── _log/                          运行时日志（gitignore）
├── .gitignore
└── README.md                      ← 本文档
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

## AI 润色（DeepSeek）

### 配置

`.env` 文件（位于 `bilihelperCore/.env`，已被 `.gitignore` 忽略，真实 key 不提交）：

```
DEEPSEEK_API_KEY=sk-...
DEEPSEEK_BASE_URL=https://api.deepseek.com
DEEPSEEK_MODEL=deepseek-v4-flash
```

模板见 `.env.example`。

### 核心逻辑

- 手动触发：当前分P 未整理时，AI润色 TAB 显示空状态卡片，点击按钮启动整理
- **每分P 一次调用**，不分块（单分P 最大约 5000 token，上下文充足）
- **支持多分P 并发整理**：每个分P 独立子进程 + CancellationToken，互不干扰
- AI 返回 JSON 的 `response_format` 强制，解析失败自动重试 1 次
- 已整理的分P 显示段落列表，按钮禁用；切换分P 实时加载对应 read.json 数据

### 交互细节

- 「原始字幕」与「AI润色」两个 TAB 按分P 记忆——每个分P 记住自己最后使用的 TAB，切走再切回能还原
- 整理中按钮隐藏、显示进度文本；切走分P 不打断后台整理，切回可见结果
- 整理失败的错误提示显示在 TAB 内容区顶部，可重试

---

## 多主题（浅 / 深色）

WPF 端支持浅色 / 深色两种主题，运行时可切换（标题栏 ☀️/🌙 按钮），选择持久化到 `%LocalAppData%\BiliHelper\theme.txt`，下次启动自动恢复。

### 结构

- `Themes/Light.xaml`、`Themes/Dark.xaml`：两套**相同 key** 的 `SolidColorBrush`（各 34 个）。key 保持一致是主题切换的前提——替换字典后所有 `DynamicResource` 同时刷新。
- `Themes/ScrollBar.xaml`：自定义细滚动条（8px、圆角 thumb），颜色通过 `DynamicResource` 随主题切换，与浅/深共用。
- `ThemeManager.cs`：切换时用正则 `Light\.xaml$|Dark\.xaml$` 定位**主题字典**并替换其 `Source`（不会误改共享的 ScrollBar 字典），从而触发全部 `DynamicResource` 刷新。

### 关键约定（勿破坏）

- **主界面颜色一律用 `DynamicResource`**（主题切换时即时刷新）。
- `Binding.Converter` 属性不是依赖属性，**不能**用 `DynamicResource`，因此 `BoolToVis` / `InverseBoolToVis` / `NullToVis` / `StatusToColor` 这类 converter 引用必须用 `StaticResource`（定义在 `Window.Resources` 内）。
- 事件色转换器（`StatusToColorConverter`）通过 `Application.Current.TryFindResource` 运行时取色，因此能随主题变化。
- `ScrollBar.xaml` 中被引用的 `x:Key` 样式必须声明在隐式样式**之前**（`StaticResource` 只能向前解析）。

---
- 每次启动覆盖，记录完整数据流：
  - 字幕抓取：启动、meta、每分P、完成
  - AI 润色：开始、子进程启动、stdout/stderr、完成回调、read.json 保存、取消/异常
- 适合排查问题时开启，测试完成后可移除细粒度日志

---

## 依赖

| 层级 | 技术 | 版本 |
|------|------|------|
| 后端运行时 | Python | ≥ 3.13 |
| 包管理 | uv | — |
| 字幕下载 | yt-dlp | ≥ 2024.1 |
| AI 调用 | openai (DeepSeek 兼容接口) | ≥ 2.53.0 |
| 桌面框架 | .NET WPF | net10.0-windows |
| 核心依赖 | 零 NuGet 包 | 纯 .NET 原生 |

---

## 环境搭建

```shell
# Python 依赖
cd bilihelperCore
uv sync

# 复制 API key 模板并填写真实值
copy .env.example .env

# WPF 构建（项目根目录）
cd BiliHelperWpf
dotnet build
```
