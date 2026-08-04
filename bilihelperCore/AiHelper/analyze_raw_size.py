"""analyze_raw_size.py — 大 raw.json 结构统计脚本（只输出数字，不打印字幕内容）。

用途:
    评估单分P 的字幕规模，为 DeepSeek 分块策略提供数据依据。

用法:
    uv run python BiliHelperCore/AiHelper/analyze_raw_size.py
    uv run python BiliHelperCore/AiHelper/analyze_raw_size.py --raw <path> --part 3
    uv run python BiliHelperCore/AiHelper/analyze_raw_size.py --json
    uv run python BiliHelperCore/AiHelper/analyze_raw_size.py --batch-entries 100 --token-budget 3000

说明:
    - 绝不输出任何字幕文本内容，只输出统计数字
    - token 估算采用 "字符数 / chars_per_token" 的线性近似
      (中文大致 1.4 字符 ≈ 1 token，可 --chars-per-token 调整)
    - 分块估算给出两种口径:
        entries 口径: 按条数切批 (默认 200 条/批)
        token 口径:   按预计 token 用量贪心切批 (默认 4000 token/批)
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys
from pathlib import Path

DEFAULT_RAW = (
    Path(__file__).resolve().parents[2]
    / "BiliHelperWpf"
    / "history"
    / "20260730"
    / "BV1MFokBkEzC"
    / "raw.json"
)


def count_lines(path: Path) -> int:
    """二进制分块数行数，避免一次性读入。"""
    total = 0
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            total += chunk.count(b"\n")
    return total


def est_tokens(chars: int, cpt: float) -> int:
    return max(1, math.ceil(chars / cpt))


def batch_by_entries(count: int, size: int) -> int:
    return math.ceil(count / size) if count else 0


def batch_by_tokens(entries: list[dict], budget: int, cpt: float) -> int:
    """按估算 token 贪心切批；单个超大字幕单独成批。"""
    if not entries:
        return 0
    batches = 0
    cur = 0
    for e in entries:
        t = est_tokens(len(e.get("Text") or e.get("text") or ""), cpt)
        if cur + t > budget and cur > 0:
            batches += 1
            cur = 0
        cur += t
    if cur > 0:
        batches += 1
    return batches


def fmt_duration(sec: float | None) -> str:
    if sec is None:
        return "--:--"
    s = int(sec)
    h, rem = divmod(s, 3600)
    m, s = divmod(rem, 60)
    return f"{h:d}:{m:02d}:{s:02d}" if h else f"{m:02d}:{s:02d}"


def load_parts(raw_path: Path) -> tuple[dict, list[dict]]:
    data = json.loads(raw_path.read_text(encoding="utf-8-sig"))
    video = (
        data.get("data", data) if isinstance(data, dict) and "data" in data else data
    )
    parts = video.get("Parts") or video.get("parts") or []
    return video, parts


def analyze_part(part: dict, cpt: float) -> dict:
    entries = part.get("Entries") or part.get("entries") or []
    texts = [(e.get("Text") or e.get("text") or "") for e in entries]

    total_chars = sum(len(t) for t in texts)
    max_chars = max((len(t) for t in texts), default=0)

    return {
        "part_number": part.get("PartNumber") or part.get("part_number") or 1,
        "part_title": part.get("PartTitle") or part.get("part_title") or "",
        "duration": part.get("Duration") or part.get("duration"),
        "subtitle_count": len(entries),
        "total_chars": total_chars,
        "est_tokens": est_tokens(total_chars, cpt),
        "max_single_chars": max_chars,
        "avg_chars": round(total_chars / len(entries), 1) if entries else 0,
    }


def build_summary(
    raw_path: Path,
    cpt: float,
    batch_entries: int,
    token_budget: int,
    part_filter: int | None,
) -> dict:
    video, parts = load_parts(raw_path)

    title = video.get("Title") or video.get("title") or ""
    bv_id = video.get("BvId") or video.get("bv_id") or raw_path.stem
    total_parts = len(parts)

    part_stats = [analyze_part(p, cpt) for p in parts]
    if part_filter is not None:
        part_stats = [s for s in part_stats if s["part_number"] == part_filter]

    for s in part_stats:
        # 需要原文 entries 才能做 token 口径切批，这里从 parts 里重新定位
        part = next(
            (
                p
                for p in parts
                if (p.get("PartNumber") or p.get("part_number")) == s["part_number"]
            ),
            None,
        )
        entries = part.get("Entries") or part.get("entries") or []
        s["batches_entries"] = batch_by_entries(s["subtitle_count"], batch_entries)
        s["batches_tokens"] = batch_by_tokens(entries, token_budget, cpt)

    totals = {
        "parts_count": len(part_stats),
        "subtitle_count": sum(s["subtitle_count"] for s in part_stats),
        "total_chars": sum(s["total_chars"] for s in part_stats),
        "est_tokens": sum(s["est_tokens"] for s in part_stats),
        "max_single_chars": max((s["max_single_chars"] for s in part_stats), default=0),
    }

    return {
        "file_size_bytes": os.path.getsize(raw_path),
        "file_lines": count_lines(raw_path),
        "bv_id": bv_id,
        "title": title,
        "total_parts": total_parts,
        "chars_per_token": cpt,
        "batch_entries_size": batch_entries,
        "token_budget": token_budget,
        "part_filter": part_filter,
        "parts": part_stats,
        "totals": totals,
    }


def print_summary(s: dict) -> None:
    mb = s["file_size_bytes"] / (1024 * 1024)
    print(f"文件   : {s['file_size_bytes']} 字节 ({mb:.1f} MB, {s['file_lines']} 行)")
    print(f"视频   : {s['bv_id']} | {s['title'] or '(无标题)'}")
    print(f"分P数  : {s['total_parts']}  (统计 {s['totals']['parts_count']} 个)")
    print(
        f"估算   : 字符/token = {s['chars_per_token']}, "
        f"按 {s['token_budget']} token/批, 按 {s['batch_entries_size']} 条/批"
    )
    if s["part_filter"] is not None:
        print(f"过滤   : 仅 P{s['part_filter']}")
    print()

    header = (
        f"{'P':<4}{'条数':<8}{'时长':<9}{'总字符':<12}"
        f"{'估token':<10}{'单条最长':<10}{'批(条)':<8}{'批(token)':<10}"
    )
    print(header)
    print("-" * len(header))

    for p in s["parts"]:
        print(
            f"P{p['part_number']:<3}"
            f"{p['subtitle_count']:<8}"
            f"{fmt_duration(p['duration']):<9}"
            f"{p['total_chars']:<12,}"
            f"{p['est_tokens']:<10,}"
            f"{p['max_single_chars']:<10,}"
            f"{p['batches_entries']:<8}"
            f"{p['batches_tokens']:<10}"
        )

    print("-" * len(header))
    t = s["totals"]
    print(
        f"合计   {t['subtitle_count']:<8}"
        f"{'':<9}{t['total_chars']:<12,}{t['est_tokens']:<10,}"
        f"{t['max_single_chars']:<10,}"
    )
    print()
    print("提示: 单条最长 决定是否需要单独处理超大字幕；批数用于评估调用次数与费用。")


def main() -> int:
    parser = argparse.ArgumentParser(
        description="大 raw.json 结构统计（不打印字幕内容）"
    )
    parser.add_argument("--raw", default=str(DEFAULT_RAW), help="raw.json 路径")
    parser.add_argument("--part", type=int, default=None, help="只统计指定分P")
    parser.add_argument(
        "--chars-per-token",
        type=float,
        default=1.4,
        help="估算用: 多少字符约等于 1 token (默认 1.4)",
    )
    parser.add_argument(
        "--batch-entries", type=int, default=200, help="按条数切批的每批条数 (默认 200)"
    )
    parser.add_argument(
        "--token-budget",
        type=int,
        default=4000,
        help="按 token 切批的每批预算 (默认 4000)",
    )
    parser.add_argument("--json", action="store_true", help="输出 JSON 格式摘要")
    args = parser.parse_args()

    raw_path = Path(args.raw)
    if not raw_path.is_file():
        print(f"[ERROR] 找不到 raw.json: {raw_path}")
        return 1

    summary = build_summary(
        raw_path,
        cpt=args.chars_per_token,
        batch_entries=args.batch_entries,
        token_budget=args.token_budget,
        part_filter=args.part,
    )

    if args.json:
        print(json.dumps(summary, ensure_ascii=False, indent=2))
    else:
        print_summary(summary)

    return 0


if __name__ == "__main__":
    sys.exit(main())
