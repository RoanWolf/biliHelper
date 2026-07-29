"""BiliHelper CLI 入口。"""

from bili_helper import (
    BiliHelperError,
    NetworkError,
    VideoNotFoundError,
    get_parts,
    get_subtitles,
    get_subtitles_flat,
    get_subtitles_stream,
)

if __name__ == "__main__":
    import argparse
    import json
    import sys
    from pathlib import Path

    parser = argparse.ArgumentParser(description="Bilibili 字幕提取工具")
    parser.add_argument("url", help="Bilibili 视频 URL")
    parser.add_argument(
        "-c", "--cookies", default=None, help="Cookies 文件路径 (netscape 格式)"
    )
    parser.add_argument("-p", "--parts-only", action="store_true")
    parser.add_argument("-f", "--flat", action="store_true")
    parser.add_argument(
        "--stream", action="store_true", help="流式输出，逐分P 打印 JSON 行"
    )
    parser.add_argument("-o", "--output", default=None)

    args = parser.parse_args()

    try:
        if args.stream:
            for item in get_subtitles_stream(args.url, cookies=args.cookies):
                line = json.dumps(item, ensure_ascii=False, default=str)
                print(line, flush=True)
        elif args.parts_only:
            parts, _ = get_parts(args.url, cookies=args.cookies)
            out = json.dumps(parts, ensure_ascii=False, indent=2)
        elif args.flat:
            entries = get_subtitles_flat(args.url, cookies=args.cookies)
            out = json.dumps(entries, ensure_ascii=False, indent=2)
        else:
            data = get_subtitles(args.url, cookies=args.cookies)
            out = json.dumps(data, ensure_ascii=False, indent=2, default=str)

        if args.output and not args.stream:
            Path(args.output).write_text(out, encoding="utf-8")
            print(f"→ {args.output}")
        elif not args.stream:
            print(out)

    except (VideoNotFoundError, NetworkError, BiliHelperError) as e:
        print(f"[{type(e).__name__}] {e}", file=sys.stderr)
        raise SystemExit(1)
