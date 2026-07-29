"""
BiliHelper — 从 Bilibili 视频提取字幕为结构化 JSON。

用法:
    from bili_helper import get_subtitles

    data = get_subtitles("BV_URL", cookies="cookies.txt")

返回结构:
    {
      "status": "ok" | "partial" | "empty",
      "bv_id": "BV...",
      "title": "视频标题",
      "parts": [
        {
          "part_number": 1,
          "part_title": "...",
          "duration": 1234.5,
          "subtitle_count": 1786,
          "subtitle_source": "ai",
          "subtitle_lang": "ai-zh",
          "entries": [
            {"index": 1, "start_time": 0.38, "end_time": 3.24, "text": "..."},
            ...
          ]
        }
      ]
    }

边缘情况:
    - 视频不存在        → VideoNotFoundError
    - 网络/超时         → NetworkError
    - 字幕需要登录但无cookie → 返回 parts 但没有 subtitles
    - 视频无字幕        → 返回 parts 但没有 subtitles
    - CC 字幕 vs AI 字幕 → subtitle_source 字段区分
    - 多P 视频(分集)    → 每P 独立 entry
    - cookie 过期       → 同「字幕需要登录」
    - 部分P 有字幕/部分没有 → status="partial"
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Optional

# ---------------------------------------------------------------------------
# 路径 & 常量
# ---------------------------------------------------------------------------

_PROJECT_DIR = Path(__file__).resolve().parent
_YTDLP = str(_PROJECT_DIR / ".venv" / "Scripts" / "yt-dlp.exe")

_SUB_LANGS_PREFER = ["ai-zh", "zh-Hans", "zh-Hant", "zh", "ai-en", "en", "ja"]

_SRT_TIME_RE = re.compile(
    r"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})\s*-->\s*"
    r"(\d{2}):(\d{2}):(\d{2})[,.](\d{3})"
)


# ---------------------------------------------------------------------------
# 异常
# ---------------------------------------------------------------------------


class BiliHelperError(Exception):
    """基础异常。"""


class VideoNotFoundError(BiliHelperError):
    """视频不存在 / BV 号无效。"""


class NetworkError(BiliHelperError):
    """网络错误 / 超时。"""


# ---------------------------------------------------------------------------
# 内部工具
# ---------------------------------------------------------------------------


def _run_ytdlp(args: list[str], timeout: int = 120) -> subprocess.CompletedProcess:
    """运行 yt-dlp，统一编码。"""
    env = os.environ.copy()
    env["PYTHONIOENCODING"] = "utf-8"
    try:
        return subprocess.run(
            [_YTDLP] + args,
            capture_output=True,
            text=True,
            timeout=timeout,
            encoding="utf-8",
            errors="replace",
            env=env,
        )
    except subprocess.TimeoutExpired:
        raise NetworkError(f"yt-dlp 超时 ({timeout}s)") from None
    except FileNotFoundError:
        raise BiliHelperError(f"找不到 yt-dlp: {_YTDLP}\n请先运行: uv sync") from None


def _cookies_arg(cookies: Optional[str] = None) -> list[str]:
    if cookies is None:
        return []
    p = Path(cookies)
    if p.is_file():
        return ["--cookies", str(p)]
    return ["--cookies-from-browser", cookies]


def _ts(h: str, m: str, s: str, ms: str) -> float:
    return int(h) * 3600 + int(m) * 60 + int(s) + int(ms) / 1000


def _classify_source(lang: str) -> str:
    """根据语言代码推断字幕来源 (best-effort)."""
    if lang.startswith("ai-"):
        return "ai"
    if lang in ("danmaku",):
        return "danmaku"
    return "manual"


# ---------------------------------------------------------------------------
# SRT 解析
# ---------------------------------------------------------------------------


def parse_srt(text: str) -> list[dict]:
    """解析 SRT → [{index, start_time, end_time, text}]。"""
    entries: list[dict] = []
    for block in text.strip().split("\n\n"):
        lines = block.strip().split("\n")
        if len(lines) < 2:
            continue
        try:
            idx = int(lines[0].strip())
        except ValueError:
            continue
        m = _SRT_TIME_RE.search(lines[1])
        if not m:
            continue
        start = _ts(m.group(1), m.group(2), m.group(3), m.group(4))
        end = _ts(m.group(5), m.group(6), m.group(7), m.group(8))
        content = "\n".join(lines[2:]).strip()
        if not content:
            continue
        entries.append(
            {"index": idx, "start_time": start, "end_time": end, "text": content}
        )
    return entries


# ---------------------------------------------------------------------------
# 分P 信息
# ---------------------------------------------------------------------------


def get_parts(url: str, cookies: Optional[str] = None) -> tuple[list[dict], str]:
    """列出所有分P。

    Returns:
        [{"part_number": 1, "part_title": "...", "duration": 123.4, "webpage_url": "..."}, ...]
    """
    args = ["--flat-playlist", "--dump-json", "--skip-download", "--yes-playlist"]
    args += _cookies_arg(cookies) + [url]

    result = _run_ytdlp(args)

    if result.returncode != 0:
        stderr = result.stderr or ""
        if "not found" in stderr.lower() or "404" in stderr:
            raise VideoNotFoundError(f"视频不存在或无法访问: {url}")
        if any(
            kw in stderr.lower() for kw in ("timeout", "reset", "connection refused")
        ):
            raise NetworkError(f"网络错误: {stderr.strip()}")
        raise BiliHelperError(f"yt-dlp 错误:\n{stderr}")

    parts = []
    playlist_title = ""
    for line in result.stdout.strip().split("\n"):
        if not line:
            continue
        info = json.loads(line)
        if not playlist_title:
            playlist_title = info.get("playlist_title") or info.get("playlist") or ""
        parts.append(
            {
                "part_number": info.get("playlist_index") or 1,
                "part_title": info.get("title") or "",
                "duration": info.get("duration"),
                "webpage_url": info.get("webpage_url", url),
            }
        )
    return parts, playlist_title


# ---------------------------------------------------------------------------
# 核心：字幕提取
# ---------------------------------------------------------------------------


def get_subtitles(
    url: str,
    cookies: Optional[str] = None,
    prefer_langs: Optional[list[str]] = None,
) -> dict:
    """一键提取视频所有分P的字幕。

    Args:
        url: Bilibili 视频 URL
        cookies: cookies 文件路径 (netscape 格式) 或浏览器名
        prefer_langs: 语言偏好顺序

    Returns:
        {
            "status": "ok"|"partial"|"empty",
            "bv_id": "BV...",
            "title": "标题",
            "total_parts": 1,
            "parts": [{
                "part_number": 1,
                "part_title": "...",
                "duration": 123.4,
                "subtitle_count": 0,        # 0 = 无字幕
                "subtitle_source": "ai",    # 有字幕时才出现
                "subtitle_lang": "ai-zh",   # 有字幕时才出现
                "entries": [...]           # 有字幕时才出现
            }, ...]
        }
    """
    if prefer_langs is None:
        prefer_langs = list(_SUB_LANGS_PREFER)

    # ── Step 1: 获取分P 信息 ────────────────────────────────────
    parts_info, playlist_title = get_parts(url, cookies=cookies)
    if not parts_info:
        raise BiliHelperError("未获取到任何分P信息")

    total_parts = len(parts_info)

    # ── Step 2: 批量下载所有分P 字幕 ─────────────────────────────
    with tempfile.TemporaryDirectory(prefix="bili_helper_") as tmpdir:
        is_bv_url = "/video/BV" in url or "/video/bv" in url.lower()
        bv_id = _extract_bv_from_url(url)

        # 模板: yt-dlp 产出 001_BVxxxxx.ai-zh.srt 这类文件
        tmpl = str(Path(tmpdir) / "%(playlist_index)03d_%(display_id)s")

        dl_args = (
            [
                "--write-subs",
                "--write-auto-subs",
                "--sub-langs",
                "all,-danmaku",
                "--sub-format",
                "srt",
                "--skip-download",
                "--yes-playlist",
                "--no-mtime",
                "-o",
                tmpl,
            ]
            + _cookies_arg(cookies)
            + [url]
        )

        # 多P 视频需要更长的超时（每P 约 3-5秒）
        dl_timeout = max(120, total_parts * 10)
        dl_result = _run_ytdlp(dl_args, timeout=dl_timeout)

        # ── Step 3: 解析下载的 SRT 文件 ───────────────────────────
        # 按 part_number 分组: {1: [("zh", entries)], 2: [("en", entries)], ...}
        sub_by_part: dict[int, list[tuple[str, list[dict]]]] = {}
        for f in Path(tmpdir).glob("*.srt"):
            fname = f.name  # e.g. "001_BVxxxxx.zh.srt"
            # 提取 part_number 和 lang
            part_str = fname.split("_")[0]  # "001"
            lang = f.stem.rsplit(".", 1)[-1] if "." in f.stem else "unknown"
            try:
                part_num = int(part_str)
            except ValueError:
                part_num = 1  # fallback

            srt_text = f.read_text(encoding="utf-8")
            entries = parse_srt(srt_text)
            if entries:
                sub_by_part.setdefault(part_num, []).append((lang, entries))

        # ── Step 4: 组装结果 ──────────────────────────────────────
        title = ""
        parts_data: list[dict] = []
        any_subs = False
        all_subs = True

        for pi in parts_info:
            pn = pi["part_number"]

            if not title and pn == 1:
                # 用 flat dump-json 的 title 作为视频总标题
                title = pi.get("part_title", "")
                # 如果是多P 视频，可能需要从 --dump-json (非 flat) 取 fulltitle
                # 这里先 fallback
                if total_parts > 1 and not bv_id:
                    bv_id = _extract_bv_from_url(pi.get("webpage_url", url))

            candidates = sub_by_part.get(pn, [])

            part_entry: dict = {
                "part_number": pn,
                "part_title": pi["part_title"],
                "duration": pi.get("duration"),
                "subtitle_count": 0,
            }

            if candidates:
                # 按 prefer_langs 选出最佳字幕
                chosen_lang = None
                chosen_entries = None
                for lang in prefer_langs:
                    for cl, entries in candidates:
                        if cl == lang:
                            chosen_lang = cl
                            chosen_entries = entries
                            break
                    if chosen_lang:
                        break
                # 未匹配到偏好语言，取第一个可用的
                if not chosen_lang:
                    chosen_lang = candidates[0][0]
                    chosen_entries = candidates[0][1]

                part_entry["subtitle_count"] = len(chosen_entries)
                part_entry["subtitle_source"] = _classify_source(chosen_lang)
                part_entry["subtitle_lang"] = chosen_lang
                part_entry["entries"] = chosen_entries
                any_subs = True
            else:
                all_subs = False

            parts_data.append(part_entry)

        # 标题优先级: playlist_title > 第一个分P的 title
        if not title:
            title = playlist_title or parts_data[0].get("part_title", "")

    # ── Step 5: 汇总状态 ────────────────────────────────────────
    if not any_subs:
        status = "empty"
    elif all_subs:
        status = "ok"
    else:
        status = "partial"

    return {
        "status": status,
        "bv_id": bv_id or _extract_bv_from_url(url),
        "title": title,
        "total_parts": total_parts,
        "parts": parts_data,
    }


def get_subtitles_flat(
    url: str,
    cookies: Optional[str] = None,
    prefer_langs: Optional[list[str]] = None,
) -> list[dict]:
    """与 get_subtitles 相同，但把所有分P 的条目拼成一个大数组。

    每条额外带 _part_number 和 _part_title 标记来源。
    """
    data = get_subtitles(url, cookies=cookies, prefer_langs=prefer_langs)
    all_entries: list[dict] = []
    for part in data["parts"]:
        entries = part.get("entries", [])
        for e in entries:
            e["_part_number"] = part["part_number"]
            e["_part_title"] = part["part_title"]
        all_entries.extend(entries)
    return all_entries


# ---------------------------------------------------------------------------
# 流式输出（逐分P 加载，给 WPF 用）
# ---------------------------------------------------------------------------


def get_subtitles_stream(
    url: str,
    cookies: Optional[str] = None,
    prefer_langs: Optional[list[str]] = None,
):
    """流式提取字幕，每处理完一个分P 立即 yield。

    用于 WPF GUI 实现逐P 加载，不必等待全部下载完。

    Yields:
        dict，包含 "type" 字段:
        - {"type":"meta", "title":"...", "bv_id":"...", "total_parts":N}
        - {"type":"part", "part_number":N, "entries":[...], ...}
        - {"type":"complete", "status":"ok"|"partial"|"empty"}
    """
    if prefer_langs is None:
        prefer_langs = list(_SUB_LANGS_PREFER)

    parts_info, playlist_title = get_parts(url, cookies=cookies)
    if not parts_info:
        raise BiliHelperError("未获取到任何分P信息")

    bv_id = _extract_bv_from_url(url)
    title = playlist_title or parts_info[0].get("part_title", "")
    total_parts = len(parts_info)

    yield {"type": "meta", "title": title, "bv_id": bv_id, "total_parts": total_parts}

    any_subs = False
    all_subs = True

    with tempfile.TemporaryDirectory(prefix="bili_helper_") as tmpdir:
        for pi in parts_info:
            pn = pi["part_number"]
            part_url = pi.get("webpage_url", url)

            # ----- 先用 --dump-json 获取真实分P标题和时长 -----
            real_title = pi.get("part_title", "")
            real_duration = pi.get("duration")
            try:
                dump_args = (
                    ["--dump-json", "--skip-download"]
                    + _cookies_arg(cookies)
                    + [part_url]
                )
                dump_result = _run_ytdlp(dump_args, timeout=30)
                if dump_result.returncode == 0:
                    info = json.loads(dump_result.stdout.strip().split("\n")[0])
                    full_title = info.get("title") or ""
                    if full_title:
                        # B站标题格式: "课程总标题 p01 分P标题"
                        m = re.search(r"\bp\d+\s+(.*)", full_title)
                        real_title = m.group(1) if m else full_title
                    real_duration = info.get("duration") or real_duration
            except Exception:
                pass

            # ----- 下载字幕（不用 --print，避免冲突）-----
            tmpl = str(Path(tmpdir) / f"{pn:03d}_%(display_id)s")

            dl_args = (
                [
                    "--write-subs",
                    "--write-auto-subs",
                    "--sub-langs",
                    "all,-danmaku",
                    "--sub-format",
                    "srt",
                    "--skip-download",
                    "--no-mtime",
                    "-o",
                    tmpl,
                ]
                + _cookies_arg(cookies)
                + [part_url]
            )

            try:
                dl_result = _run_ytdlp(dl_args, timeout=120)
            except (NetworkError, BiliHelperError):
                all_subs = False
                yield {
                    "type": "part",
                    "part_number": pn,
                    "part_title": real_title,
                    "duration": real_duration,
                    "subtitle_count": 0,
                }
                continue

            part_entry = {
                "type": "part",
                "part_number": pn,
                "part_title": real_title,
                "duration": real_duration,
                "subtitle_count": 0,
            }

            candidates = []
            for f in Path(tmpdir).glob(f"{pn:03d}_*.srt"):
                lang = f.stem.rsplit(".", 1)[-1] if "." in f.stem else "unknown"
                srt_text = f.read_text(encoding="utf-8")
                entries = parse_srt(srt_text)
                if entries:
                    candidates.append((lang, entries))

            if candidates:
                chosen_lang = None
                chosen_entries = None
                for lang in prefer_langs:
                    for cl, entries in candidates:
                        if cl == lang:
                            chosen_lang = cl
                            chosen_entries = entries
                            break
                    if chosen_lang:
                        break
                if not chosen_lang:
                    chosen_lang = candidates[0][0]
                    chosen_entries = candidates[0][1]

                part_entry["subtitle_count"] = len(chosen_entries)
                part_entry["subtitle_source"] = _classify_source(chosen_lang)
                part_entry["subtitle_lang"] = chosen_lang
                part_entry["entries"] = chosen_entries
                any_subs = True
            else:
                all_subs = False

            yield part_entry

    if not any_subs:
        status = "empty"
    elif all_subs:
        status = "ok"
    else:
        status = "partial"

    yield {"type": "complete", "status": status}


# ---------------------------------------------------------------------------
# 辅助
# ---------------------------------------------------------------------------


def _extract_bv_from_url(url: str) -> str:
    m = re.search(r"BV[\w]+", url, re.IGNORECASE)
    return m.group(0) if m else url


# ---------------------------------------------------------------------------
# 命令行入口
# ---------------------------------------------------------------------------

if __name__ == "__main__":
    import argparse

    parser = argparse.ArgumentParser(description="Bilibili 字幕提取工具")
    parser.add_argument("url", help="Bilibili 视频 URL")
    parser.add_argument(
        "-c", "--cookies", default=None, help="Cookies 文件路径 (netscape 格式)"
    )
    parser.add_argument("-p", "--parts-only", action="store_true", help="只列出分P信息")
    parser.add_argument(
        "-f", "--flat", action="store_true", help="简化输出：所有条目拼成一个大数组"
    )
    parser.add_argument(
        "-o", "--output", default=None, help="输出 JSON 文件路径 (默认 stdout)"
    )

    args = parser.parse_args()

    try:
        if args.parts_only:
            parts, _ = get_parts(args.url, cookies=args.cookies)
            out = json.dumps(parts, ensure_ascii=False, indent=2)
        elif args.flat:
            entries = get_subtitles_flat(args.url, cookies=args.cookies)
            out = json.dumps(entries, ensure_ascii=False, indent=2)
        else:
            data = get_subtitles(args.url, cookies=args.cookies)
            out = json.dumps(data, ensure_ascii=False, indent=2, default=str)

        if args.output:
            Path(args.output).write_text(out, encoding="utf-8")
            print(f"→ {args.output}")
        else:
            print(out)

    except BiliHelperError as e:
        print(f"[错误] {e}", file=__import__("sys").stderr)
        raise SystemExit(1)
