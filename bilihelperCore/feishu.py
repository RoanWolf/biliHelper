#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""feishu.py — 飞书云文档同步子进程（供 WPF 调用，stdout JSONL）。

子命令：
  feishu.py test
      连通性测试：取 token + 往群发一条测试消息。
      stdout: {"type":"complete","ok":true,"message":"..."} 或 {"type":"error","message":"..."}
  feishu.py sync --bv-dir <视频历史目录> --part <分P号>
      把某分P 的 AI 润色结果同步为飞书云文档：
      取 token → 顶层/视频文件夹 find-or-create(+群授权) → 文档新建(带B站封面)或覆盖正文
      → 发群链接消息 → 更新映射。
      stdout: {"type":"status","step":"..."} … {"type":"complete","document_url":"..."}
              {"type":"error","message":"..."}（exit 1）

环境变量（WPF 注入，缺失回退默认）：
  FEISHU_APP_ID / FEISHU_APP_SECRET / FEISHU_CHAT_ID    必填
  FEISHU_ROOT_FOLDER   顶层文件夹名，默认 "BiliHelper"
  FEISHU_SYNC_FILE     映射文件路径（默认 %LocalAppData%/BiliHelper/feishu_sync.json；测试可覆盖）

映射文件 feishu_sync.json（防重复创建的核心，原子写）:
  {"root_folder_name", "root_folder_token", "videos": {bv_id: {"folder_token", "parts": {n: {"document_id", ...}}}}}
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import time
from pathlib import Path

import requests

for _s in (sys.stdout, sys.stderr):
    try:
        _s.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass

API_BASE = "https://open.feishu.cn/open-apis"
DOC_URL_TMPL = "https://feishu.cn/docx/{id}"

APP_DIR = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "BiliHelper"
DEFAULT_SYNC_FILE = APP_DIR / "feishu_sync.json"

BLOCK_BATCH = 50      # 单次写入文档的 block 上限（保守分批）
NAME_MAX_LEN = 40     # 文件夹名截断
DOC_TITLE_MAX = 80    # 文档标题截断
_USER_AGENT = ("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
               "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36")
_INVALID_NAME_CHARS = re.compile(r'[\\/:*?"<>|\r\n\t]')


def emit(data: dict) -> None:
    """stdout 一行 JSON，立即 flush（WPF 逐行读取）。"""
    print(json.dumps(data, ensure_ascii=False), flush=True)


def log(msg: str) -> None:
    print(f"[feishu] {msg}", file=sys.stderr, flush=True)


def env_or(name: str, default: str = "") -> str:
    return (os.environ.get(name) or default).strip()


def clean_name(name: str, max_len: int = NAME_MAX_LEN) -> str:
    """清洗飞书文件夹/文档名：非法字符替换为 _，超长截断。"""
    name = _INVALID_NAME_CHARS.sub("_", (name or "").strip())
    if len(name) > max_len:
        name = name[:max_len].rstrip()
    return name or "未命名"


class FeishuError(Exception):
    """飞书 API 调用失败。"""


# ─────────────────────────────────────────────────────────────
# 基础 API
# ─────────────────────────────────────────────────────────────

def get_token(app_id: str, app_secret: str) -> str:
    r = requests.post(
        f"{API_BASE}/auth/v3/tenant_access_token/internal",
        json={"app_id": app_id, "app_secret": app_secret},
        timeout=15,
    )
    r.raise_for_status()
    data = r.json()
    if data.get("code") != 0:
        raise FeishuError(f"获取 token 失败: code={data.get('code')} {data.get('msg')}")
    return data["tenant_access_token"]


def _headers(token: str) -> dict:
    return {"Authorization": f"Bearer {token}"}


def check_ok(resp: requests.Response, what: str) -> dict:
    if resp.status_code != 200:
        raise FeishuError(f"{what} HTTP {resp.status_code}: {resp.text[:200]}")
    data = resp.json()
    if data.get("code") != 0:
        raise FeishuError(f"{what} code={data.get('code')} msg={data.get('msg')}")
    return data


def api_get(token: str, path: str, params: dict | None = None) -> requests.Response:
    return requests.get(f"{API_BASE}{path}", params=params or {},
                        headers=_headers(token), timeout=30)


def api_post(token: str, path: str, params: dict | None = None,
             body: dict | None = None) -> requests.Response:
    return requests.post(f"{API_BASE}{path}", params=params or {},
                         headers=_headers(token), json=body or {}, timeout=60)


def api_patch(token: str, path: str, body: dict) -> requests.Response:
    return requests.patch(f"{API_BASE}{path}", headers=_headers(token),
                          json=body, timeout=60)


def api_delete(token: str, path: str, body: dict | None = None) -> requests.Response:
    return requests.delete(f"{API_BASE}{path}", headers=_headers(token),
                           json=body or {}, timeout=60)


def send_message(token: str, chat_id: str, text: str) -> None:
    check_ok(
        api_post(token, "/im/v1/messages",
                 params={"receive_id_type": "chat_id"},
                 body={"receive_id": chat_id, "msg_type": "text",
                       "content": json.dumps({"text": text}, ensure_ascii=False)}),
        "发送群消息")


def send_document_card(token: str, chat_id: str, title: str, part_line: str,
                       meta: str, doc_url: str) -> None:
    """发 interactive 卡片消息：标题 + 分P 名 + 作者/元信息 + 「打开文档」链接按钮。"""
    content = {
        "config": {"wide_screen_mode": True},
        "header": {
            "template": "blue",
            "title": {"tag": "plain_text", "content": title},
        },
        "elements": [
            {
                "tag": "div",
                "text": {"tag": "lark_md", "content": part_line},
            },
            {
                "tag": "div",
                "text": {"tag": "lark_md", "content": meta},
            },
            {
                "tag": "action",
                "actions": [
                    {
                        "tag": "button",
                        "text": {"tag": "plain_text", "content": "打开文档"},
                        "type": "primary",
                        "url": doc_url,
                    },
                ],
            },
        ],
    }
    check_ok(
        api_post(token, "/im/v1/messages",
                 params={"receive_id_type": "chat_id"},
                 body={"receive_id": chat_id, "msg_type": "interactive",
                       "content": json.dumps(content, ensure_ascii=False)}),
        "发送卡片消息")


# ─────────────────────────────────────────────────────────────
# 云盘文件夹
# ─────────────────────────────────────────────────────────────

def find_folder(token: str, parent_token: str, name: str) -> str | None:
    """在 parent 文件夹下按名找文件夹（分页），返回 token 或 None。"""
    page_token = ""
    while True:
        params = {"folder_token": parent_token, "page_size": 100}
        if page_token:
            params["page_token"] = page_token
        data = check_ok(api_get(token, "/drive/v1/files", params), "列文件夹")
        body = data.get("data") or {}
        for f in body.get("files") or []:
            if f.get("type") == "folder" and f.get("name") == name:
                return f.get("token")
        if not body.get("has_more"):
            return None
        page_token = body.get("next_page_token") or ""
        if not page_token:
            return None


def create_folder(token: str, parent_token: str, name: str) -> str:
    data = check_ok(
        api_post(token, "/drive/v1/files/create_folder",
                 body={"name": name, "folder_token": parent_token}),
        "创建文件夹")
    return data["data"]["token"]


def grant_chat(token: str, folder_token: str, chat_id: str) -> None:
    """把群加为文件夹协作者 full_access（幂等，重复调用无害）。"""
    check_ok(
        api_post(token, f"/drive/v1/permissions/{folder_token}/members",
                 params={"type": "folder"},
                 body={"member_type": "openchat", "member_id": chat_id,
                       "perm": "full_access"}),
        "群授权")


# ─────────────────────────────────────────────────────────────
# 文档
# ─────────────────────────────────────────────────────────────

def create_document(token: str, folder_token: str, title: str) -> str:
    data = check_ok(
        api_post(token, "/docx/v1/documents",
                 body={"folder_token": folder_token, "title": title}),
        "创建文档")
    return data["data"]["document"]["document_id"]


def write_paragraphs(token: str, document_id: str, paragraphs: list[str]) -> None:
    """把段落写入文档根块下（每批 ≤ BLOCK_BATCH）。"""
    for i in range(0, len(paragraphs), BLOCK_BATCH):
        chunk = paragraphs[i:i + BLOCK_BATCH]
        children = [
            {"block_type": 2,
             "text": {"elements": [{"text_run": {"content": p}}], "style": {}}}
            for p in chunk
        ]
        check_ok(
            api_post(token,
                     f"/docx/v1/documents/{document_id}/blocks/{document_id}/children",
                     body={"children": children}),
            "写入正文")


def clear_document(token: str, document_id: str) -> None:
    """清空文档根块下的全部子块（覆盖更新前调用，保留 document_id）。

    用 children/batch_delete 按索引范围删除（删除后索引左移，故每轮删头部一批）。
    """
    while True:
        data = check_ok(
            api_get(token,
                    f"/docx/v1/documents/{document_id}/blocks/{document_id}/children",
                    {"page_size": 500}),
            "列文档块")
        items = (data.get("data") or {}).get("items") or []
        n = len(items)
        if n == 0:
            break
        count = min(BLOCK_BATCH, n)
        check_ok(
            api_delete(token,
                       f"/docx/v1/documents/{document_id}/blocks/{document_id}"
                       f"/children/batch_delete",
                       body={"start_index": 0, "end_index": count}),
            "清空文档块")


# ─────────────────────────────────────────────────────────────
# 封面
# ─────────────────────────────────────────────────────────────

def download_cover(cover_url: str) -> bytes | None:
    """下载 B 站封面（实测无需 cookie；加 Referer 保险）。失败返回 None 不阻塞。"""
    if not cover_url:
        return None
    try:
        r = requests.get(
            cover_url,
            headers={"User-Agent": _USER_AGENT, "Referer": "https://www.bilibili.com/"},
            timeout=30,
        )
        r.raise_for_status()
        return r.content
    except Exception as e:  # noqa: BLE001
        log(f"封面下载失败(继续无封面): {e}")
        return None


def upload_cover(token: str, document_id: str, image_bytes: bytes) -> str:
    """上传封面素材（parent_type=docx_image，parent_node=文档ID），返回 file_token。"""
    resp = requests.post(
        f"{API_BASE}/drive/v1/medias/upload_all",
        headers=_headers(token),
        data={"file_name": "cover.jpg",
              "parent_type": "docx_image",
              "parent_node": document_id,
              "size": str(len(image_bytes)),
              "extra": json.dumps({"drive_route_token": document_id})},
        files={"file": ("cover.jpg", image_bytes, "image/jpeg")},
        timeout=120,
    )
    data = check_ok(resp, "上传封面")
    return data["data"]["file_token"]


def set_document_cover(token: str, document_id: str, file_token: str) -> None:
    """设置文档封面（PATCH documents + update_cover）。"""
    check_ok(
        api_patch(token, f"/docx/v1/documents/{document_id}",
                  body={"update_cover": {"cover": {"token": file_token}}}),
        "设置封面")


# ─────────────────────────────────────────────────────────────
# 同步映射
# ─────────────────────────────────────────────────────────────

def load_sync_map(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError):
        return {"root_folder_name": "", "root_folder_token": "", "videos": {}}


def save_sync_map(path: Path, data: dict) -> None:
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        tmp = path.with_suffix(path.suffix + ".tmp")
        tmp.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")
        os.replace(tmp, path)
    except OSError as e:
        log(f"保存映射失败(不阻塞): {e}")


# ─────────────────────────────────────────────────────────────
# 子命令
# ─────────────────────────────────────────────────────────────

def cmd_test(args) -> int:
    app_id = env_or("FEISHU_APP_ID")
    app_secret = env_or("FEISHU_APP_SECRET")
    chat_id = env_or("FEISHU_CHAT_ID")
    if not (app_id and app_secret and chat_id):
        emit({"type": "error", "message": "飞书配置不完整（AppID/Secret/群号必填）"})
        return 1
    try:
        token = get_token(app_id, app_secret)
        send_message(token, chat_id, "BiliHelper 飞书配置测试 ✓")
        emit({"type": "complete", "ok": True, "message": "测试消息已发送"})
        return 0
    except Exception as e:  # noqa: BLE001
        emit({"type": "error", "message": str(e)})
        return 1


def cmd_sync(args) -> int:
    app_id = env_or("FEISHU_APP_ID")
    app_secret = env_or("FEISHU_APP_SECRET")
    chat_id = env_or("FEISHU_CHAT_ID")
    root_name = clean_name(env_or("FEISHU_ROOT_FOLDER", "BiliHelper"), 60)
    sync_file = Path(os.environ.get("FEISHU_SYNC_FILE") or DEFAULT_SYNC_FILE)
    if not (app_id and app_secret and chat_id):
        emit({"type": "error", "message": "飞书配置不完整（AppID/Secret/群号必填）"})
        return 1

    bv_dir = Path(args.bv_dir)
    part_no = int(args.part)

    # ── 读本地数据（index.json 元信息 + read.json 段落）──
    try:
        idx = json.loads((bv_dir / "index.json").read_text(encoding="utf-8"))
        rd = json.loads((bv_dir / "read.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as e:
        emit({"type": "error", "message": f"读取本地数据失败: {e}"})
        return 1

    meta = idx.get("Meta") or {}
    video_title = meta.get("Title") or bv_dir.name
    bv_id = meta.get("BvId") or bv_dir.name
    cover_url = idx.get("CoverUrl") or ""
    uploader = idx.get("Uploader") or ""
    part_meta = next((p for p in (idx.get("Parts") or [])
                      if p.get("PartNumber") == part_no), None)
    part_title = (part_meta or {}).get("PartTitle") or f"P{part_no}"

    paragraphs: list[str] = []
    for rp in rd.get("parts") or []:
        if rp.get("part_number") == part_no:
            paragraphs = [p.get("text") or "" for p in (rp.get("paragraphs") or [])
                          if (p.get("text") or "").strip()]
            break
    if not paragraphs:
        emit({"type": "error",
              "message": f"P{part_no} 没有可同步的段落（未整理或 read.json 缺失）"})
        return 1

    sync_map = load_sync_map(sync_file)

    try:
        emit({"type": "status", "step": "获取 tenant_access_token"})
        token = get_token(app_id, app_secret)

        # 顶层文件夹名变化 → 重置映射（重新 find-or-create）
        if sync_map.get("root_folder_name") != root_name or not sync_map.get("root_folder_token"):
            sync_map = {"root_folder_name": root_name, "root_folder_token": "", "videos": {}}
            save_sync_map(sync_file, sync_map)

        emit({"type": "status", "step": f"定位顶层文件夹「{root_name}」"})
        root_token = sync_map.get("root_folder_token") or find_folder(token, "", root_name)
        if not root_token:
            root_token = create_folder(token, "", root_name)
            grant_chat(token, root_token, chat_id)
            emit({"type": "status", "step": "已创建顶层文件夹并授权群"})
        sync_map["root_folder_token"] = root_token

        # ── 视频子文件夹 ──
        folder_name = clean_name(video_title)
        emit({"type": "status", "step": f"定位视频文件夹「{folder_name}」"})
        vid = sync_map.setdefault("videos", {}).setdefault(
            bv_id, {"folder_token": "", "folder_name": folder_name, "parts": {}})
        if not vid.get("folder_token"):
            vid["folder_token"] = find_folder(token, root_token, folder_name)
            if not vid["folder_token"]:
                vid["folder_token"] = create_folder(token, root_token, folder_name)
                grant_chat(token, vid["folder_token"], chat_id)
                emit({"type": "status", "step": "已创建视频文件夹并授权群"})

        # ── 文档：新建（带封面）或覆盖正文 ──
        doc_title = clean_name(f"P{part_no} {part_title}", DOC_TITLE_MAX)
        parts_map = vid.setdefault("parts", {})
        doc_id = (parts_map.get(str(part_no)) or {}).get("document_id")
        if doc_id:
            emit({"type": "status", "step": f"覆盖更新文档「{doc_title}」正文"})
            clear_document(token, doc_id)
            write_paragraphs(token, doc_id, paragraphs)
        else:
            emit({"type": "status", "step": f"新建文档「{doc_title}」"})
            doc_id = create_document(token, vid["folder_token"], doc_title)
            if cover_url:
                emit({"type": "status", "step": "下载 B 站封面"})
                cover_bytes = download_cover(cover_url)
                if cover_bytes:
                    emit({"type": "status", "step": "上传并设置封面"})
                    ft = upload_cover(token, doc_id, cover_bytes)
                    set_document_cover(token, doc_id, ft)
            write_paragraphs(token, doc_id, paragraphs)
            parts_map[str(part_no)] = {"document_id": doc_id}
        parts_map[str(part_no)].update({
            "doc_title": doc_title,
            "subtitle_count": len(paragraphs),
            "synced_at": time.strftime("%Y-%m-%dT%H:%M:%S"),
        })

        # ── 发群卡片消息（标题 + 分P 名 + 作者 + 打开文档按钮）──
        emit({"type": "status", "step": "发送群消息"})
        doc_url = DOC_URL_TMPL.format(id=doc_id)
        card_title = clean_name(f"《{video_title}》 P{part_no}", 80)
        card_part = f"**P{part_no} · {part_title}**"
        author_part = f"UP主：**{uploader}** · " if uploader else ""
        card_meta = (f"{author_part}{len(paragraphs)} 段 · "
                     f"整理于 {time.strftime('%m-%d %H:%M')}")
        send_document_card(token, chat_id, card_title, card_part, card_meta, doc_url)

        save_sync_map(sync_file, sync_map)
        emit({"type": "complete", "document_id": doc_id, "document_url": doc_url})
        return 0
    except FeishuError as e:
        save_sync_map(sync_file, sync_map)  # 已完成的步骤尽量保存
        emit({"type": "error", "message": str(e)})
        return 1
    except Exception as e:  # noqa: BLE001
        emit({"type": "error", "message": f"同步异常: {e}"})
        return 1


def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(prog="feishu", description="飞书云文档同步子进程")
    sub = p.add_subparsers(dest="cmd", required=True)

    t = sub.add_parser("test", help="连通性测试：取 token + 发测试消息到群")
    t.set_defaults(func=cmd_test)

    s = sub.add_parser("sync", help="同步某分P 的 AI 润色结果到飞书云文档")
    s.add_argument("--bv-dir", required=True, help="视频历史目录（含 index.json/read.json）")
    s.add_argument("--part", required=True, type=int, help="分P 号")
    s.set_defaults(func=cmd_sync)

    args = p.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
