"""
analyze_sub.py — 分析字幕 JSON，输出元信息、全文本和时间轴采样。

用法:
    python analyze_sub.py <raw_json_path> [--output-dir <dir>] [--interval <seconds>]

示例:
    python analyze_sub.py notes/OS-2026/raw/13_raw.json
    python analyze_sub.py notes/OS-2026/raw/13_raw.json --interval 120
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def analyze(
    json_path: str | Path,
    output_dir: str | Path = "temp",
    interval: float = 150.0,
) -> None:
    json_path = Path(json_path)
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    with json_path.open(encoding="utf-8") as f:
        data = json.load(f)

    # ── 元信息 ────────────────────────────────────
    print(f"Title: {data['title']}")
    print(f"BV: {data['bv_id']}")
    print(f"Total parts: {data['total_parts']}")

    for p in data["parts"]:
        duration = p["duration"]
        count = p.get("subtitle_count", 0)
        print(f"  Part: {p['part_title']}")
        print(f"  Duration: {duration:.0f}s ({duration / 60:.0f}min)")
        print(f"  Subtitle count: {count}")

    if data["status"] == "empty" or data["total_parts"] == 0:
        print("\n[WARNING] No subtitles found in this JSON.")
        return

    entries = data["parts"][0].get("entries", [])
    if not entries:
        print("\n[WARNING] 'entries' key missing or empty.")
        return

    print(f"\nTotal entries: {len(entries)}")

    # ── 输出完整文本 ─────────────────────────────
    stem = json_path.stem  # e.g. "13_raw"
    full_txt = output_dir / f"full_{stem}.txt"
    with full_txt.open("w", encoding="utf-8") as f:
        for e in entries:
            f.write(f"[{e['start_time']:.1f}s] {e['text']}\n")
    print(f"Full text → {full_txt}")

    # ── 时间轴采样 ───────────────────────────────
    print(f"\n{'═' * 70}")
    print(f"Time-slice sampling (every {interval:.0f}s):")
    print(f"{'═' * 70}")

    last_printed = -interval
    for e in entries:
        ts = e["start_time"]
        if ts < last_printed + interval:
            continue

        idx = entries.index(e)
        start_idx = max(0, idx - 2)
        end_idx = min(len(entries), idx + 3)
        snippets = [ee["text"] for ee in entries[start_idx:end_idx]]
        line = " │ ".join(snippets)
        print(f"\n[{ts:.0f}s / {ts / 60:.0f}min]  {line}")

        last_printed = ts


def main() -> None:
    parser = argparse.ArgumentParser(
        description="分析 BiliHelper 字幕 JSON，输出元信息、全文本和时间轴采样"
    )
    parser.add_argument("json_path", help="raw JSON 文件路径")
    parser.add_argument(
        "-o", "--output-dir", default="temp", help="全文本输出目录 (默认: temp)"
    )
    parser.add_argument(
        "-i",
        "--interval",
        type=float,
        default=150.0,
        help="时间轴采样间隔(秒) (默认: 150)",
    )
    args = parser.parse_args()

    analyze(args.json_path, output_dir=args.output_dir, interval=args.interval)


if __name__ == "__main__":
    main()
