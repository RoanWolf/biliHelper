# BiliHelper

B 站字幕提取 + 桌面查看器。Python 后端负责字幕抓取，WPF 桌面端负责展示和本地历史管理。

---

## 项目结构

```
biliHelper/
├── bilihelperCore/              ← Python 后端（纯数据管道）
│   ├── main.py                   CLI 入口
│   ├── bili_helper.py            字幕提取核心逻辑
│   ├── analyze_sub.py            字幕分析工具
│   ├── pyproject.toml            uv 项目配置
│   ├── uv.lock                   依赖锁
│   ├── .venv/                    虚拟环境（含 yt-dlp）
│   └── www.bilibili.com_cookies.txt 登录态
│
├── BiliHelperWpf/                ← WPF 桌面端（.NET 10）
│   ├── Models/                   数据模型
│   │   ├── BiliVideoInfo.cs      顶层视频信息
│   │   ├── PartInfo.cs           分P 信息
│   │   ├── SubtitleEntry.cs      单条字幕
│   │   ├── HistoryItem.cs        历史记录条目
│   │   ├── HistoryGroup.cs       历史记录日期分组
│   │   └── StreamEvent.cs        流式事件模型
│   ├── Services/
│   │   ├── BiliService.cs        子进程管理 + 管道读取
│   │   └── HistoryService.cs     本地 JSON 文件存储
│   ├── ViewModels/
│   │   ├── MainViewModel.cs      主视图模型（状态管理）
│   │   ├── RelayCommand.cs       ICommand 实现
│   │   └── ViewModelBase.cs      INotifyPropertyChanged 基类
│   ├── Converters/
│   │   ├── BoolToVisibilityConverter.cs
│   │   ├── NullToVisibilityConverter.cs
│   │   └── StatusToColorConverter.cs
│   ├── MainWindow.xaml           主界面
│   ├── MainWindow.xaml.cs        代码隐藏
│   ├── WpfHelper.cs              可视化树工具方法
│   └── Assets/logo.jpg           应用图标
│
├── .gitignore
├── README.md                     ← 本文档
└── SKILL.md
```

---

## 架构：管道流式传输

### 核心理念

**Python 是一个纯数据管道，不负责持久化、不负责 UI、不负责缓存。**
**WPF 负责展示、状态管理、本地存储。**

### 调用链路

```
用户点击「拉取字幕」
    │
    ▼
MainViewModel.FetchAsync(url)
    │
    ▼
BiliService.FetchStreamAsync(url, onMeta, onPart, onComplete, ...)
    │
    ├── Process.Start("uv", ["run", "python", "main.py", url, "-c", "cookies.txt", "--stream"])
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

### 管道数据格式（JSONL）

每行一个独立 JSON 对象，Python 端 `flush=True` 保证实时推送。

```
时间 →
stdout管道
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━

第1行 →  {"type":"meta", "title":"黑马...", "bv_id":"BV116...", "total_parts":29}
          WPF: 创建 VideoInfo 容器，显示元信息

第2行 →  {"type":"part", "part_number":1, "subtitle_count":176,
          "subtitle_source":"ai", "subtitle_lang":"ai-zh",
          "entries":[{"index":1,"start_time":0.0,"end_time":3.36,"text":"..."}, ...]}
          WPF: 追加 PartInfo，分P列表出现 P1

第3行 →  {"type":"part", "part_number":2, "subtitle_count":182, "entries":[...]}
          WPF: 追加 PartInfo，分P列表出现 P2

...（以此类推）

第N行 →  {"type":"complete", "status":"ok"}
          WPF: 隐藏进度条，刷新状态，保存历史记录
```

### 传输方式

| 概念 | 实现 |
|------|------|
| **传输介质** | 操作系统管道（pipe），非 HTTP、非文件 |
| **格式** | JSONL（每行一个完整 JSON） |
| **实时性** | Python 端 `flush=True` 确保每行立即推送 |
| **WPF 读取** | 两个 `Task.Run`：stdout（数据）、stderr（进度） |
| **进程关系** | WPF 父进程创建 Python 子进程，子进程退出后管道关闭 |

---

## 历史记录存储

WPF 负责，Python 不参与。

```
WPF onComplete 回调时
    │
    ├─ 内存中的 BiliVideoInfo
    │
    └─ HistoryService.SaveFromVideoInfo(info)
           │
           ├─ 序列化为 JSON
           └─ 写入 history/YYYYMMDD/BVxxx.json

存储路径：BiliHelperWpf/history/
         ├── 20260728/
         │   ├── BV1sC516rEQs.json
         │   └── BV116w5zuEbo.json
         └── 20260729/
             └── BVxxxxxxx.json
```

- 文件夹名 = 日期键（YYYYMMDD），即索引
- 文件名 = BV ID，天然唯一
- 每个 JSON 含 `meta`（索引信息）+ `data`（完整视频数据）
- 无数据库、无额外索引文件

---

## 依赖

| 层级 | 技术 | 版本 |
|------|------|------|
| 后端运行时 | Python | ≥ 3.13 |
| 包管理 | uv | — |
| 字幕下载 | yt-dlp | 2026.7.4 |
| 桌面框架 | .NET WPF | net10.0-windows |
| 核心依赖 | 零 NuGet 包 | 纯 .NET 原生 |
