"""测试脚本：读取 raw.json 并调用 DeepSeek 整理字幕。

用法:
    cd biliHelper
    uv run python BiliHelperCore/AiHelper/test_deepseek.py
    uv run python BiliHelperCore/AiHelper/test_deepseek.py --raw <path> --part 2
    uv run python BiliHelperCore/AiHelper/test_deepseek.py --output result.json
"""

import argparse
import json
import sys
from pathlib import Path

# 使脚本在任何工作目录下都能导入 AiHelper 包
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from AiHelper.reading import AIClient

# 强制 stdout/stderr 使用 UTF-8，避免 Windows 终端 GBK 编码导致乱码或崩溃
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# 默认测试数据：挑选的最小 raw.json（单P、31条字幕）
DEFAULT_RAW = (
    Path(__file__).resolve().parents[2]
    / "BiliHelperWpf"
    / "history"
    / "20260730"
    / "BV1f7366GEBe"
    / "raw.json"
)

PROMPT_TEMPLATE = """你是字幕整理助手。

任务：
将下面B站AI字幕转换为适合阅读的Markdown文本。

要求：
1. 不总结
2. 不删除内容
3. 不改变原意
4. 保留原视频表达顺序
5. 修正明显错别字
6. 添加合理标点和分段

输出必须为json，示例：{
  "title": "蜜雪冰城单品实力排行",
  "paragraphs": [
    {
      "id": 1,
      "text": "蜜桃四季春。这个我现在喝，我就感觉里面桃皮老多了。",
      "source_start_index": 2,
      "source_end_index": 5
    },
    {
      "id": 2,
      "text": "你怎么回事呢，我之前喝的时候感觉没这么多桃皮啊。",
      "source_start_index": 6,
      "source_end_index": 8
    }
  ]
}

视频标题：__TITLE__

字幕内容（每条字幕前的 [索引] 即 source_index）：

__SUBTITLES__
"""


def build_subtitle_text(entries: list[dict]) -> str:
    """把字幕条目转成带索引的文本，供 AI 引用。"""
    lines = []
    for e in entries:
        idx = e.get("Index") or e.get("index")
        text = e.get("Text") or e.get("text", "")
        lines.append(f"[{idx}] {text}")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description="测试 DeepSeek 字幕整理")
    parser.add_argument("--raw", default=str(DEFAULT_RAW), help="raw.json 路径")
    parser.add_argument("--part", type=int, default=1, help="要处理的分P (默认 1)")
    parser.add_argument(
        "--output",
        default=None,
        help="结果 JSON 输出路径（默认: AiHelper/test_data/<bv>_P<part>_result.json）",
    )
    args = parser.parse_args()

    raw_path = Path(args.raw)
    if not raw_path.is_file():
        print(f"[ERROR] 找不到 raw.json: {raw_path}")
        return 1

    # raw.json 可能带 UTF-8 BOM，用 utf-8-sig 读取可自动剥离
    data = json.loads(raw_path.read_text(encoding="utf-8-sig"))

    # 兼容两种格式：
    #   新格式 { meta, data }    → 取 data
    #   旧格式（直接是视频信息） → 直接用
    video = data.get("data") if isinstance(data, dict) and "data" in data else data

    title = video.get("Title") or video.get("title", "")
    parts = video.get("Parts") or video.get("parts", [])

    if not parts:
        print("[ERROR] raw.json 中没有分P数据")
        return 1

    target = next(
        (
            p
            for p in parts
            if (p.get("PartNumber") or p.get("part_number")) == args.part
        ),
        parts[0],
    )
    entries = target.get("Entries") or target.get("entries", [])

    if not entries:
        print(f"[ERROR] P{args.part} 没有字幕条目")
        return 1

    part_no = target.get("PartNumber") or target.get("part_number")
    part_title = target.get("PartTitle") or target.get("part_title", "")
    print(f"标题: {title}")
    print(f"分P : P{part_no} · {part_title}")
    print(f"条数: {len(entries)} 条字幕")

    prompt = PROMPT_TEMPLATE.replace("__TITLE__", title).replace(
        "__SUBTITLES__", build_subtitle_text(entries)
    )

    client = AIClient.from_env()
    print("正在调用 DeepSeek ...")
    result = client.chat(prompt)

    print("\n========== DeepSeek 返回 ==========")
    print(result)

    # 解析验证
    parsed = None
    try:
        parsed = json.loads(result)
        print("\n========== 解析验证 ==========")
        print(f"标题: {parsed.get('title')}")
        paragraphs = parsed.get("paragraphs", [])
        print(f"段落数: {len(paragraphs)}")
        for p in paragraphs:
            print(
                f"  [{p.get('source_start_index')}→{p.get('source_end_index')}] {p.get('text')}"
            )
    except json.JSONDecodeError as e:
        print(f"\n[WARN] 返回内容不是合法 JSON: {e}")

    # 保存结果（parsed 解析失败时保存原始返回文本）
    out_data = parsed if parsed is not None else {"raw_output": result}

    if args.output:
        output_path = Path(args.output)
    else:
        bv_id = video.get("BvId") or video.get("bv_id") or "unknown"
        output_path = (
            Path(__file__).parent / "test_data" / f"{bv_id}_P{part_no}_result.json"
        )
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(
        json.dumps(out_data, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print(f"\n✅ 结果已保存: {output_path}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
