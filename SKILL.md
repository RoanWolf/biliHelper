# BiliHelper Skill — B站课程字幕提取与笔记生成

## 概述

输入一个 Bilibili 视频链接，自动完成：提取 AI 字幕 → 导出 JSON → 时间轴采样 → 生成结构化 Markdown 笔记。

不绑定任何特定课程、讲师或学科。适用于任何 B 站上有 AI 字幕的课程视频。

## 前置条件

### 环境

- Python ≥ 3.13
- `yt-dlp` 已安装（项目使用 `uv` 管理依赖，`.venv/Scripts/yt-dlp.exe`）
- 运行时需在项目根目录

### Cookies

部分视频需要登录才能下载字幕。项目根目录应有 cookies 文件（Netscape 格式）。

如果没有或过期，从浏览器导出新的 cookies 文件。默认文件名：`www.bilibili.com_cookies.txt`。

## 核心脚本

### `bili_helper.py`

封装 yt-dlp 的调用，提供字幕提取：

```bash
# CLI 用法
python bili_helper.py -c <cookies_file> -o <output.json> "<bilibili_url>"
```

输出为结构化 JSON，包含视频标题、BV 号、每P 时长、字幕条目（index, start_time, end_time, text）。

### `analyze_sub.py`

分析字幕 JSON，输出元信息、时间轴采样和全文本：

```bash
python analyze_sub.py <raw_json_path> -i <interval_seconds>
```

- `-i 180`：每 180 秒采样一次（默认 150）
- 在 `temp/` 下生成 `full_<name>.txt`（全部字幕，带时间戳，方便直接阅读）

## 标准工作流

### Step 1: 提取字幕

```bash
python bili_helper.py -c www.bilibili.com_cookies.txt -o <output_dir>/raw/tmp_raw.json "<bilibili_video_url>"
```

### Step 2: 分析元信息

```bash
python analyze_sub.py <output_dir>/raw/tmp_raw.json -i 180
```

输出示例：

```
Title: 13 - 多处理器编程：从入门到放弃 [2026 南京大学操作系统原理]
BV: BV1vgQGBREyJ
Total parts: 1
  Part: 13 - ...
  Duration: 4970s (83min)
  Subtitle count: 2269
```

从 `Title` 中提取**课程名称**、**讲次编号**、**本节标题**和**时长**。

### Step 3: 重命名 raw JSON

按讲次编号（从 Title 中提取）重命名：

```bash
mv <output_dir>/raw/tmp_raw.json <output_dir>/raw/XX_raw.json
```

其中 `XX` = 从 Title 中提取的编号（如 `13`）。

如果视频标题没有编号，按课程列表中的实际序号命名。

### Step 4: 阅读全文本

文件：`temp/full_tmp_raw.txt`

格式：`[秒数] 字幕文本`

**阅读策略**（按优先级）：

1. **前 100-150 行**：了解本节开场、课程安排、结构预告——这是确定笔记章节划分的关键
2. **每 ~300 行跳读一段**（约 8-10 分钟）：掌握整体内容的起承转合
3. **关键段落精读**：新概念引入、现场演示、总结部分
4. **用 Step 2 的采样输出**定位每个大块的**精确起始时间**

### Step 5: 撰写笔记

笔记结构和元素参见下方「笔记规范」。

### Step 6: 清理

```bash
rm -f temp/full_tmp_raw.txt
```

## 笔记规范

### 标题

```markdown
# XX — 中文标题

> 课程全称 (年份) | 讲师姓名 | ~XX分钟
```

课程信息从视频 Title 中提取（如 `[2026 南京大学操作系统原理]`）。

### 目录

列出所有章节及时间戳，格式：

```markdown
1. [章节名](#1-章节名-MMSSMMSS)
```

锚点格式：`#序号-章节名拼音首字母小写-起始时间戳`。

时间戳用 `MMSSMMSS`（如 `00001249` = 00:00–12:49；如章节跨越至结尾则省略结束时间，如 `MMSS`）。

### 章节标题

```markdown
## 1. 章节名 (MM:SS–MM:SS)
```

时间戳格式：`MM:SS`（分和秒各占两位，不足补零）。

### 正文元素

核心目标是**从字幕中提炼结构化知识**，而非逐字转录。原则：

1. **提取核心概念**：每个概念用一句话概括，复杂概念附简短解释
2. **保留技术细节**：命令行示例、代码片段、寄存器名称、参数值等照实记录
3. **引用金句**：讲师的原话如果表达精准或有感染力，用 `>` 引用
4. **组织结构**：用表格对比概念，用 ASCII 图示（`┌──┐`）画架构图
5. **标注时间戳段落**：在自然的内容转折处标注时间范围

```markdown
> "这是一句被引用的话。"
```

```
ASCII 架构图示例：
┌──────────┐    ┌──────────┐
│  组件 A   │───→│  组件 B   │
└──────────┘    └──────────┘
```

### 结尾固定内容

```markdown
### X.Y 关键概念速查

| 概念 | 一句话 |
|------|--------|
| **概念名** | 一句话解释 |

---

## 相关资料

- 论文、手册、工具链接（整理笔记时如果有明显相关的资源就列出）
```

### 笔记长度

- 80-90 分钟课程：通常 500-700 行
- 60 分钟课程：通常 350-500 行
- 30 分钟课程：通常 200-300 行

## 边缘情况

### 无字幕

如果 `analyze_sub.py` 输出 `Subtitle count: 0` 或 `status: "empty"`：

```bash
.venv/Scripts/yt-dlp.exe --list-subs --cookies <cookies> "<url>"
```

如果只有 `danmaku xml` 没有其他字幕格式 → **标记为「无字幕，跳过」**，删除无效 raw JSON：

```bash
rm <output_dir>/raw/XX_raw.json
```

### 讲次缺失

课程可能跳过某些编号。正常现象——在汇总时标注「缺失」即可，继续处理下一节。

### 多P 视频

`bili_helper.py` 自动处理。每P 的条目中带 `_part_number` 和 `_part_title`。笔记中按分P 组织，每P 独立一节或在一个大节下分小节。

## 效率提示

- **采样间隔**：80-90 分钟用 `-i 180`（~25 个采样点），60 分钟用 `-i 120`，30 分钟用 `-i 90`
- **批量提取**：收集 playlist 中所有链接，写简单脚本循环调用 `bili_helper.py`
- **先采样后精读**：采样输出快速判断内容结构，只对关键段落精读全文

## 文件结构（参考）

```
biliHelper/
├── bili_helper.py          # 字幕提取脚本
├── analyze_sub.py          # 分析脚本
├── main.py                 # CLI 入口
├── SKILL.md                # 本文件
├── www.bilibili.com_cookies.txt
├── notes/
│   └── <课程名>/
│       ├── raw/            # 原始字幕 JSON
│       │   ├── 01_raw.json
│       │   └── ...
│       ├── 01-第一讲标题.md
│       └── ...
└── temp/                   # 临时文件