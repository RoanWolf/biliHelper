"""
analyze_sub.py — 分析字幕数据（新格式 index.json + parts/），输出元信息、全文本和时间轴采样。

独立诊断工具，不属于 WPF 管线（WPF 通过 stdout JSONL 与 Python 交互，不经过本脚本）。

用法:
    python analyze_sub.py <BV目录路径> [--part N] [--output-dir <dir>] [--interval <seconds>]

示例:
    python analyze_sub.py BiliHelperWpf/history/20260816/BV1DD4y127r4
    python analyze_sub.py BiliHelperWpf/history/20260816/BV1DD4y127r4 --interval 120
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def _load(bv_dir: Path) -> tuple[dict, list[dict]]:
    """读新格式 index.json + parts/NNN.json。

    Returns:
        (meta, parts) — parts 为 [{meta: {PartNumber, PartTitle, Duration, ...}, entries: [...]}]。
    """
    idx = json.loads((bv_dir / "index.json").read_text(encoding="utf-8"))
    meta = idx.get("Meta") or {}
    parts: list[dict] = []
    for pm in idx.get("Parts") or []:
        pn = pm.get("PartNumber")
        part_file = bv_dir / "parts" / f"{pn:03d}.json"
        part = {}
        if part_file.is_file():
            part = json.loads(part_file.read_text(encoding="utf-8"))
        parts.append(
            {
                "meta": {
                    **pm,
                    "PartTitle": pm.get("PartTitle") or part.get("PartTitle") or "",
                },
                "entries": part.get("Entries") or [],
            }
        )
    return meta, parts


def analyze(
    bv_dir: str | Path,
    output_dir: str | Path = "temp",
    interval: float = 150.0,
    part_number: int | None = None,
) -> None:
    bv_dir = Path(bv_dir)
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)

    meta, parts = _load(bv_dir)

    # ── 元信息 ────────────────────────────────────
    print(f"Title: {meta.get('Title')}")
    print(f"BV: {meta.get('BvId')}")
    print(f"Total parts: {meta.get('TotalParts')}")

    for p in parts:
        m = p["meta"]
        duration = m.get("Duration") or 0
        count = m.get("SubtitleCount") or 0
        print(f"  Part: P{m.get('PartNumber')} {m.get('PartTitle')}")
        print(f"  Duration: {duration:.0f}s ({duration / 60:.0f}min)")
        print(f"  Subtitle count: {count}")

    if not parts:
        print("\n[WARNING] 没有分P数据.")
        return

    # ── 选择要分析的分P：--part 指定，否则第一个有字幕的 ──
    target = None
    if part_number is not None:
        for p in parts:
            if p["meta"].get("PartNumber") == part_number:
                target = p
                break
        if target is None:
            print(f"\n[WARNING] 找不到分P P{part_number}.")
            return
    else:
        target = next((p for p in parts if p["entries"]), parts[0])

    entries = target["entries"]
    m = target["meta"]
    print(f"\n分析分P: P{m.get('PartNumber')} {m.get('PartTitle')} ({len(entries)} 条字幕)")

    if not entries:
        print("\n[WARNING] 该分P无字幕.")
        return

    # ── 输出完整文本 ─────────────────────────────
    stem = f"{meta.get('BvId') or bv_dir.name}_P{m.get('PartNumber')}"
    full_txt = output_dir / f"full_{stem}.txt"
    with full_txt.open("w", encoding="utf-8") as f:
        for e in entries:
            f.write(f"[{e['StartTime']:.1f}s] {e['Text']}\n")
    print(f"Full text → {full_txt}")

    # ── 时间轴采样 ───────────────────────────────
    print(f"\n{'═' * 70}")
    print(f"Time-slice sampling (every {interval:.0f}s):")
    print(f"{'═' * 70}")

    last_printed = -interval
    for i, e in enumerate(entries):
        ts = e["StartTime"]
        if ts < last_printed + interval:
            continue

        start_idx = max(0, i - 2)
        end_idx = min(len(entries), i + 3)
        snippets = [ee["Text"] for ee in entries[start_idx:end_idx]]
        line = " │ ".join(snippets)
        print(f"\n[{ts:.0f}s / {ts / 60:.0f}min]  {line}")

        last_printed = ts


def main() -> None:
    parser = argparse.ArgumentParser(
        description="分析 BiliHelper 字幕数据（index.json + parts/），输出元信息、全文本和时间轴采样"
    )
    parser.add_argument("bv_dir", help="视频历史目录路径（含 index.json 与 parts/）")
    parser.add_argument("--part", type=int, default=None, help="要分析的分P 号（默认第一个有字幕的分P）")
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

    analyze(args.bv_dir, output_dir=args.output_dir, interval=args.interval, part_number=args.part)


if __name__ == "__main__":
    main()
