"""ai_read.py — 单分P AI 阅读版生成脚本（供 WPF 子进程调用）。

WPF 子进程调用契约:
    uv run python AiHelper/ai_read.py --raw <raw.json路径> --part <分P号>

stdout 逐行输出 JSONL（UTF-8，每行 flush）:
    {"type":"meta",     "bv_id":"...", "title":"...", "part_number":1,
     "part_title":"...", "subtitle_count":31}
    {"type":"complete", "part_number":1, "status":"ok", "paragraphs":[...]}

stderr 输出进度文本（WPF 进度条显示，非结构化）:
    正在调用 DeepSeek (P1 · 31 条字幕)...
    整理完成，13 个段落。

退出码:
    0  成功
    1  失败（错误信息在 stderr，格式 "[ERROR] ..."）
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

# 确保能导入 AiHelper 包（无论工作目录在哪）
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from AiHelper.reading import AIClient

# 强制 stdout/stderr 使用 UTF-8，避免 Windows GBK 终端乱码/崩溃
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# AI 返回非法 JSON 时的重试次数（网络类异常不重试，避免重复计费）
RETRY_ON_JSON_ERROR = 1

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


def emit(obj: dict) -> None:
    """向 stdout 输出一行 JSON，并立即 flush。"""
    print(json.dumps(obj, ensure_ascii=False, default=str), flush=True)


def progress(msg: str) -> None:
    """向 stderr 输出进度文本。"""
    print(msg, file=sys.stderr, flush=True)


def load_parts(raw_path: Path) -> tuple[dict, list[dict]]:
    """读取 raw.json，兼容新格式 {meta,data} 与旧格式（直接是视频信息）。"""
    data = json.loads(raw_path.read_text(encoding="utf-8-sig"))
    video = (
        data.get("data", data) if isinstance(data, dict) and "data" in data else data
    )
    parts = video.get("Parts") or video.get("parts") or []
    return video, parts


def find_part(parts: list[dict], part_number: int) -> dict | None:
    for p in parts:
        if (p.get("PartNumber") or p.get("part_number")) == part_number:
            return p
    return None


def get_entries(part: dict) -> list[dict]:
    return part.get("Entries") or part.get("entries") or []


def build_subtitle_text(entries: list[dict]) -> str:
    """把字幕条目转成带索引文本，供 AI 引用。"""
    lines = []
    for e in entries:
        idx = e.get("Index") or e.get("index")
        text = e.get("Text") or e.get("text", "")
        lines.append(f"[{idx}] {text}")
    return "\n".join(lines)


def build_prompt(title: str, subtitle_text: str) -> str:
    return PROMPT_TEMPLATE.replace("__TITLE__", title or "").replace(
        "__SUBTITLES__", subtitle_text
    )


def parse_and_validate(result: str, subtitle_count: int) -> list[dict]:
    """解析 AI 返回的 JSON，校验结构与 source 索引范围。

    Args:
        result: AI 返回的 JSON 字符串。
        subtitle_count: 当前分P 字幕条数，用于 clamp 索引边界。

    Returns:
        规范化后的段落列表 [{id, text, source_start_index, source_end_index}]。

    Raises:
        ValueError: 结构不合法 / 段落为空。
    """
    try:
        parsed = json.loads(result)
    except json.JSONDecodeError as e:
        raise ValueError(f"AI 返回的不是合法 JSON: {e}") from None

    if not isinstance(parsed, dict):
        raise ValueError("AI 返回的不是 JSON 对象")

    raw_paragraphs = parsed.get("paragraphs")
    if not isinstance(raw_paragraphs, list):
        raise ValueError("AI 返回缺少 paragraphs 列表")

    # 字幕索引下界固定为 1（B站字幕从 1 开始）
    low, high = 1, max(1, subtitle_count)

    paragraphs: list[dict] = []
    for item in raw_paragraphs:
        if not isinstance(item, dict) or not isinstance(item.get("text"), str):
            continue
        text = item["text"].strip()
        if not text:
            continue

        try:
            start = int(item.get("source_start_index", 0))
            end = int(item.get("source_end_index", 0))
        except (TypeError, ValueError):
            continue

        # clamp 到有效区间，防止模型越界
        start = max(low, min(high, start))
        end = max(low, min(high, end))
        if start > end:
            start, end = end, start

        paragraphs.append(
            {
                "id": len(paragraphs) + 1,
                "text": text,
                "source_start_index": start,
                "source_end_index": end,
            }
        )

    if not paragraphs:
        raise ValueError("AI 返回的段落列表为空")

    return paragraphs


def main() -> int:
    parser = argparse.ArgumentParser(description="单分P AI 阅读版生成")
    parser.add_argument("--raw", help="raw.json 路径")
    parser.add_argument("--part", type=int, help="要处理的分P 号")
    parser.add_argument(
        "--test", action="store_true",
        help="连通性测试模式：仅验证 API key/base_url/model，不处理字幕",
    )
    args = parser.parse_args()

    # ── 连通性测试 ────────────────────────────────────────
    if args.test:
        try:
            client = AIClient.from_env()
        except ValueError as e:
            print(f"[ERROR] {e}", file=sys.stderr)
            return 1
        ok, message = client.test_connectivity()
        emit({"type": "test", "ok": ok, "message": message})
        return 0 if ok else 1

    if not args.raw or args.part is None:
        print(f"[ERROR] 缺少 --raw / --part 参数", file=sys.stderr)
        return 1

    raw_path = Path(args.raw)

    # ── 读取 raw.json ─────────────────────────────────────
    if not raw_path.is_file():
        print(f"[ERROR] 找不到 raw.json: {raw_path}", file=sys.stderr)
        return 1

    try:
        video, parts = load_parts(raw_path)
    except (json.JSONDecodeError, OSError) as e:
        print(f"[ERROR] 读取 raw.json 失败: {e}", file=sys.stderr)
        return 1

    if not parts:
        print("[ERROR] raw.json 中没有分P数据", file=sys.stderr)
        return 1

    part = find_part(parts, args.part)
    if part is None:
        print(f"[ERROR] 找不到分P P{args.part}", file=sys.stderr)
        return 1

    entries = get_entries(part)
    if not entries:
        print(f"[ERROR] P{args.part} 没有字幕条目", file=sys.stderr)
        return 1

    bv_id = video.get("BvId") or video.get("bv_id") or raw_path.stem
    title = video.get("Title") or video.get("title") or ""
    part_title = part.get("PartTitle") or part.get("part_title") or ""
    part_no = args.part

    # ── meta：WPF 收到后即可定位存储路径 ──────────────────
    emit(
        {
            "type": "meta",
            "bv_id": bv_id,
            "title": title,
            "part_number": part_no,
            "part_title": part_title,
            "subtitle_count": len(entries),
        }
    )

    prompt = build_prompt(title, build_subtitle_text(entries))

    # ── 初始化客户端 ──────────────────────────────────────
    try:
        client = AIClient.from_env()
    except ValueError as e:
        print(f"[ERROR] {e}", file=sys.stderr)
        return 1

    progress(f"正在调用 DeepSeek (P{part_no} · {len(entries)} 条字幕)...")

    # ── 调用 DeepSeek（仅 JSON 解析失败重试）──────────────
    paragraphs = None
    last_error = ""
    for attempt in range(RETRY_ON_JSON_ERROR + 1):
        try:
            result = client.chat(prompt)
            paragraphs = parse_and_validate(result, len(entries))
            break
        except Exception as e:  # noqa: BLE001 — 统一兜底，重试后报错
            last_error = str(e)
            if attempt < RETRY_ON_JSON_ERROR:
                progress(f"解析失败，重试 {attempt + 1}/{RETRY_ON_JSON_ERROR}...")

    if paragraphs is None:
        print(f"[ERROR] AI 整理失败: {last_error}", file=sys.stderr)
        return 1

    # ── complete：携带整理结果 ────────────────────────────
    emit(
        {
            "type": "complete",
            "part_number": part_no,
            "status": "ok",
            "paragraphs": paragraphs,
        }
    )

    progress(f"整理完成，{len(paragraphs)} 个段落。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
