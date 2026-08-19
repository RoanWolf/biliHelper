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
import logging
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

# ---------------------------------------------------------------------------
# 路径 & 常量
# ---------------------------------------------------------------------------

_PROJECT_DIR = Path(__file__).resolve().parent
# 用模块方式调用 yt-dlp（官方推荐）：不依赖 .venv/Scripts/yt-dlp.exe launcher
# （pip install --target 组装发布包时不会生成该 exe），版本与 site-packages 中的模块永远一致
_YTDLP_CMD = [sys.executable, "-m", "yt_dlp"]

_SUB_LANGS_PREFER = ["ai-zh", "zh-Hans", "zh-Hant", "zh", "ai-en", "en", "ja"]

# YouTube 字幕语言档位：只拉这几档（YouTube 自动翻译有 60+ 语言，全拉浪费带宽）。
# zh-Hans 经实测可命中 YouTube 自动机翻中文字幕（zh-Hans-en）。
_YOUTUBE_SUB_LANGS = "zh-Hans,zh-Hant,zh,en"

# YouTube 视频 ID 提取：watch?v= / shorts / embed / live / youtu.be
_YT_URL_RE = re.compile(
    r"(?:youtube\.com/watch\?[^#\s]*[?&]v="
    r"|youtube\.com/(?:shorts|embed|live)/"
    r"|youtu\.be/)([A-Za-z0-9_-]{11})",
    re.IGNORECASE,
)


def is_youtube(url: str) -> bool:
    """判断 URL 是否属于 YouTube。"""
    low = url.lower()
    return "youtube.com" in low or "youtu.be" in low


def _sub_langs_arg(url: str) -> str:
    """字幕语言参数：B 站拉全部（ai-zh 等命名特殊），YouTube 限中文档位。"""
    return _YOUTUBE_SUB_LANGS if is_youtube(url) else "all,-danmaku"

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
    """运行 yt-dlp（模块方式），统一编码。"""
    env = os.environ.copy()
    env["PYTHONIOENCODING"] = "utf-8"
    try:
        return subprocess.run(
            _YTDLP_CMD + list(args),
            capture_output=True,
            text=True,
            timeout=timeout,
            encoding="utf-8",
            errors="replace",
            env=env,
            check=False,
        )
    except subprocess.TimeoutExpired:
        raise NetworkError(f"yt-dlp 超时 ({timeout}s)") from None
    except FileNotFoundError:
        raise BiliHelperError(f"找不到 Python 解释器: {sys.executable}") from None


def _cookies_arg(
    cookies: str | None = None, url: str | None = None
) -> tuple[list[str], Path | None]:
    """返回 (yt-dlp cookies 参数, 临时副本路径或 None)。

    cookies 文件复制到临时副本再传给 yt-dlp：yt-dlp close() 会无条件写回
    --cookies 指向的文件，直接传正式 cookies.txt 的话，写失败（只读/被占用）
    会让整次抓取崩溃，还会改写 auth.py 原子维护的正式文件 —— 副本隔离两者。
    文件不存在时不传任何 cookie（匿名抓取）；不回退 --cookies-from-browser
    （该参数期望浏览器名，收到路径会直接报错）。
    YouTube 公开字幕匿名可拉，B 站 cookies 不适用，直接跳过。
    """
    if cookies is None or (url and is_youtube(url)):
        return [], None
    p = Path(cookies)
    if not p.is_file():
        return [], None
    fd, tmp_name = tempfile.mkstemp(prefix="bilihelper_cookie_", suffix=".txt")
    os.close(fd)
    tmp = Path(tmp_name)
    shutil.copyfile(p, tmp)
    return ["--cookies", str(tmp)], tmp


def _discard_cookie_tmp(tmp: Path | None) -> None:
    """删除临时 cookies 副本（尽力而为）。"""
    if tmp is None:
        return
    try:
        tmp.unlink(missing_ok=True)
    except OSError:
        pass


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


def get_parts(url: str, cookies: str | None = None) -> tuple[list[dict], str]:
    """列出所有分P。

    Returns:
        [{"part_number": 1, "part_title": "...", "duration": 123.4, "webpage_url": "..."}, ...]
    """
    url = _normalize_url(url)
    args = ["--flat-playlist", "--dump-json", "--skip-download", "--yes-playlist"]
    cookie_args, cookie_tmp = _cookies_arg(cookies, url)
    try:
        result = _run_ytdlp(args + cookie_args + [url])
    finally:
        _discard_cookie_tmp(cookie_tmp)

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
    cookies: str | None = None,
    prefer_langs: list[str] | None = None,
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
        bv_id = _extract_video_id(url)

        # 模板: yt-dlp 产出 001_BVxxxxx.ai-zh.srt 这类文件
        tmpl = str(Path(tmpdir) / "%(playlist_index)03d_%(display_id)s")

        cookie_args, cookie_tmp = _cookies_arg(cookies, url)
        dl_args = (
            [
                "--write-subs",
                "--write-auto-subs",
                "--sub-langs",
                _sub_langs_arg(url),
                "--sub-format",
                "srt",
                "--skip-download",
                "--yes-playlist",
                "--no-mtime",
                "-o",
                tmpl,
            ]
            + cookie_args
            + [url]
        )

        # 多P 视频需要更长的超时（每P 约 3-5秒）
        dl_timeout = max(120, total_parts * 10)
        try:
            _run_ytdlp(dl_args, timeout=dl_timeout)
        finally:
            _discard_cookie_tmp(cookie_tmp)

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

            srt_text = f.read_text(encoding="utf-8-sig")
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
                    bv_id = _extract_video_id(pi.get("webpage_url", url))

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
                chosen_entries: list[dict] = []
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
        "bv_id": bv_id or _extract_video_id(url),
        "title": title,
        "total_parts": total_parts,
        "parts": parts_data,
    }


def get_subtitles_flat(
    url: str,
    cookies: str | None = None,
    prefer_langs: list[str] | None = None,
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
    cookies: str | None = None,
    prefer_langs: list[str] | None = None,
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

    bv_id = _extract_video_id(url)
    title = playlist_title or parts_info[0].get("part_title", "")
    total_parts = len(parts_info)

    yield {"type": "meta", "title": title, "bv_id": bv_id, "total_parts": total_parts}

    any_subs = False
    all_subs = True

    cookie_args, cookie_tmp = _cookies_arg(cookies, url)

    with tempfile.TemporaryDirectory(prefix="bili_helper_") as tmpdir:
        for pi in parts_info:
            pn = pi["part_number"]
            part_url = pi.get("webpage_url", url)

            # ----- 先用 --dump-json 获取真实分P标题和时长 -----
            real_title = pi.get("part_title", "")
            real_duration = pi.get("duration")
            cover_url = ""  # B站视频封面（视频级，各分P 相同；供飞书文档封面用）
            uploader = ""   # B站 UP 主名（视频级；供飞书卡片消息作者展示用）
            try:
                dump_args = (
                    ["--dump-json", "--skip-download"]
                    + cookie_args
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
                    cover_url = info.get("thumbnail") or cover_url
                    uploader = info.get("uploader") or uploader
            except (NetworkError, BiliHelperError, json.JSONDecodeError):
                logging.getLogger(__name__).warning(
                    "获取分P详情失败，使用默认标题/时长"
                )

            # ----- 下载字幕（不用 --print，避免冲突）-----
            tmpl = str(Path(tmpdir) / f"{pn:03d}_%(display_id)s")

            dl_args = (
                [
                    "--write-subs",
                    "--write-auto-subs",
                    "--sub-langs",
                    _sub_langs_arg(url),
                    "--sub-format",
                    "srt",
                    "--skip-download",
                    "--no-mtime",
                    "-o",
                    tmpl,
                ]
                + cookie_args
                + [part_url]
            )

            try:
                _run_ytdlp(dl_args, timeout=120)
            except (NetworkError, BiliHelperError):
                all_subs = False
                yield {
                    "type": "part",
                    "part_number": pn,
                    "part_title": real_title,
                    "duration": real_duration,
                    "subtitle_count": 0,
                    "cover_url": cover_url,
                    "uploader": uploader,
                }
                continue

            part_entry = {
                "type": "part",
                "part_number": pn,
                "part_title": real_title,
                "duration": real_duration,
                "subtitle_count": 0,
                "cover_url": cover_url,
                "uploader": uploader,
            }

            candidates = []
            for f in Path(tmpdir).glob(f"{pn:03d}_*.srt"):
                lang = f.stem.rsplit(".", 1)[-1] if "." in f.stem else "unknown"
                srt_text = f.read_text(encoding="utf-8-sig")
                entries = parse_srt(srt_text)
                if entries:
                    candidates.append((lang, entries))

            if candidates:
                chosen_lang = None
                chosen_entries: list[dict] = []
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

    _discard_cookie_tmp(cookie_tmp)

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


def _extract_video_id(url: str) -> str:
    """提取视频 ID：B 站 BV 号或 YouTube 11 位 ID；提取不到则原样返回。"""
    m = re.search(r"BV[\w]+", url, re.IGNORECASE)
    if m:
        return m.group(0)
    m = _YT_URL_RE.search(url)
    if m:
        return m.group(1)
    return url


def _normalize_url(url: str) -> str:
    """裸 BV 号 → 完整 Bilibili URL；裸 11 位 YouTube ID → youtu.be URL。"""
    s = url.strip()
    if re.search(r"^BV[\w]{10,}$", s, re.IGNORECASE):
        return f"https://www.bilibili.com/video/{s}"
    if re.fullmatch(r"[A-Za-z0-9_-]{11}", s):
        return f"https://youtu.be/{s}"
    return url


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
        import sys

        print(f"[错误] {e}", file=sys.stderr)
        raise SystemExit(1)
