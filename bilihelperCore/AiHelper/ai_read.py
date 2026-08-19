"""ai_read.py — 单分P AI 阅读版生成脚本（供 WPF 子进程调用）。

WPF 子进程调用契约:
    python ai_read.py --part-file <parts/NNN.json 路径>

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
import time
from pathlib import Path

from openai import (
    APIConnectionError,
    APIStatusError,
    APITimeoutError,
)

# 确保本目录模块（reading.py）可导入：embed 发布版走 _pth 隔离模式，
# 脚本所在目录不会自动进 sys.path（与开发机 venv 模式不同）
sys.path.insert(0, str(Path(__file__).resolve().parent))

from reading import AIClient

# 强制 stdout/stderr 使用 UTF-8，避免 Windows GBK 终端乱码/崩溃
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

# 重试策略（分类，避免对确定性错误重复计费）:
#   - 网络类（断连/超时，APIConnectionError 含 APITimeoutError）: 重试 2 次
#   - 服务端 5xx（APIStatusError.status_code >= 500）: 重试 2 次
#   - JSON 解析失败: 重试 1 次
#   - 确定性错误（AuthenticationError/NotFoundError/RateLimitError/4xx 等）: 不重试
RETRY_NETWORK = 2
RETRY_JSON = 1
# 网络/5xx 重试之间的退避等待（秒）
RETRY_BACKOFF_SECONDS = 2.0

SYSTEM_PROMPT = """你是字幕整理助手。

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
"""


def emit(obj: dict) -> None:
    """向 stdout 输出一行 JSON，并立即 flush。"""
    print(json.dumps(obj, ensure_ascii=False, default=str), flush=True)


def progress(msg: str) -> None:
    """向 stderr 输出进度文本。"""
    print(msg, file=sys.stderr, flush=True)


def fail(msg: str) -> int:
    """向 stderr 输出错误并返回失败退出码。"""
    print(f"[ERROR] {msg}", file=sys.stderr, flush=True)
    return 1


def build_user_prompt(title: str, entries: list[dict]) -> str:
    """构造 user 消息内容：视频标题 + 带 [索引] 的字幕文本。

    每条字幕前的 [索引] 供模型填写 source_start_index/source_end_index。
    """
    subtitle_lines = "\n".join(
        f"[{e.get('Index') or e.get('index')}] {e.get('Text') or e.get('text', '')}"
        for e in entries
    )
    return (
        f"视频标题：{title}\n\n"
        "字幕内容（每条字幕前的 [索引] 即 source_index）：\n\n"
        f"{subtitle_lines}"
    )


def is_retryable_api_error(exc: Exception) -> bool:
    """判断是否属于值得重试的网络/服务端错误。

    网络类（APIConnectionError，含 APITimeoutError）与 5xx 服务端错误
    （代理偶发 500/503）属于瞬时故障，重试有实际意义；
    认证/模型不存在/限流/4xx 属于确定性错误，重试只会重复计费。
    """
    if isinstance(exc, (APIConnectionError, APITimeoutError)):
        return True
    if isinstance(exc, APIStatusError):
        return exc.status_code >= 500
    return False


def describe_api_error(exc: Exception) -> str:
    """生成用户可读的错误描述，异常类型未知时回退到 str()。"""
    if isinstance(exc, APITimeoutError):
        return f"连接超时: {exc}"
    if isinstance(exc, APIConnectionError):
        return f"网络连接失败: {exc}"
    if isinstance(exc, APIStatusError):
        return f"服务端返回 HTTP {exc.status_code}: {exc}"
    return str(exc)


def parse_and_validate(result: str | None, subtitle_count: int) -> list[dict]:
    """解析 AI 返回的 JSON，校验结构与 source 索引范围。

    Args:
        result: AI 返回的 JSON 字符串。
        subtitle_count: 当前分P 字幕条数，用于 clamp 索引边界。

    Returns:
        规范化后的段落列表 [{id, text, source_start_index, source_end_index}]。

    Raises:
        ValueError: 结构不合法 / 段落为空。
    """
    if result is None:
        raise ValueError("AI 返回为空") from None

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
            progress("跳过无效段落（缺少 text 字段）")
            continue
        text = item["text"].strip()
        if not text:
            progress("跳过无效段落（text 为空）")
            continue

        try:
            start = int(item.get("source_start_index", 0))
            end = int(item.get("source_end_index", 0))
        except (TypeError, ValueError):
            progress("跳过无效段落（source 索引不是整数）")
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


def run_with_retry(
    client: AIClient,
    system: str,
    user: str,
    subtitle_count: int,
) -> tuple[list[dict] | None, str]:
    """调用 DeepSeek 并按错误分类重试，返回 (段落列表, 错误描述)。

    网络/5xx 最多重试 RETRY_NETWORK 次，JSON 解析失败重试 RETRY_JSON 次，
    确定性错误（认证/4xx/限流等）不重试；总耗时上界约 15 分钟（超时兜底）。
    """
    network_attempts = 0
    json_attempts = 0
    while True:
        try:
            result = client.chat(system, user)
            return parse_and_validate(result, subtitle_count), ""
        except Exception as e:  # noqa: BLE001 — 统一捕获后按类型分类处理
            last_error = describe_api_error(e)

            if is_retryable_api_error(e):
                # 网络/5xx：最多重试 RETRY_NETWORK 次
                network_attempts += 1
                if network_attempts <= RETRY_NETWORK:
                    progress(
                        f"网络/服务端异常，{network_attempts}/{RETRY_NETWORK} 次重试中..."
                    )
                    time.sleep(RETRY_BACKOFF_SECONDS)
                    continue
                return None, last_error

            # 确定性 API 错误（401 认证/404 模型不存在/429 限流等 4xx）不重试
            if isinstance(e, APIStatusError):
                return None, last_error

            # JSON 解析失败（ValueError）最多重试 RETRY_JSON 次
            if isinstance(e, ValueError):
                json_attempts += 1
                if json_attempts <= RETRY_JSON:
                    progress(f"解析失败，{json_attempts}/{RETRY_JSON} 次重试中...")
                    continue

            # 其余未知异常：不重试，避免重复计费
            return None, last_error


def main() -> int:
    parser = argparse.ArgumentParser(description="单分P AI 阅读版生成")
    parser.add_argument(
        "--part-file", help="单分P 字幕文件路径（新格式 parts/NNN.json）"
    )
    parser.add_argument(
        "--test",
        action="store_true",
        help="连通性测试模式：仅验证 API key/base_url/model，不处理字幕",
    )
    args = parser.parse_args()

    # ── 连通性测试 ────────────────────────────────────────
    if args.test:
        try:
            client = AIClient.from_env()
        except ValueError as e:
            return fail(str(e))
        ok, message = client.test_connectivity()
        emit({"type": "test", "ok": ok, "message": message})
        return 0 if ok else 1

    # ── 读取单分P 字幕文件 ────────────────────────────────
    if not args.part_file:
        return fail("需要 --part-file 参数")
    part_path = Path(args.part_file)
    if not part_path.is_file():
        return fail(f"找不到分P文件: {part_path}")
    try:
        part = json.loads(part_path.read_text(encoding="utf-8-sig"))
    except (json.JSONDecodeError, OSError) as e:
        return fail(f"读取分P文件失败: {e}")

    entries = part.get("Entries") or []
    if not entries:
        return fail(f"P{part.get('PartNumber') or part_path.stem} 没有字幕条目")

    part_no = int(part.get("PartNumber") or part_path.stem)
    part_title = part.get("PartTitle") or ""
    # parts/NNN.json 的祖父目录即 BV 目录名
    bv_id = part_path.parent.parent.name
    # 视频总标题从同目录 index.json 读取（best-effort，读不到置空不阻塞）
    title = ""
    index_path = part_path.parent.parent / "index.json"
    if index_path.is_file():
        try:
            idx = json.loads(index_path.read_text(encoding="utf-8-sig"))
            title = (idx.get("Meta") or {}).get("Title") or ""
        except (json.JSONDecodeError, OSError):
            pass

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

    # ── 初始化客户端 ──────────────────────────────────────
    try:
        client = AIClient.from_env()
    except ValueError as e:
        return fail(str(e))

    progress(f"正在调用 DeepSeek (P{part_no} · {len(entries)} 条字幕)...")
    paragraphs, error = run_with_retry(
        client, SYSTEM_PROMPT, build_user_prompt(title, entries), len(entries)
    )
    if paragraphs is None:
        return fail(f"AI 整理失败: {error}")

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
