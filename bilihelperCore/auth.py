#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
B 站 cookie 登录 / 探测 —— 供 WPF 子进程调用。
=========================================================
子命令（WPF 调用，stdout 输出 JSONL，每行一个 JSON 对象）：
  auth.py generate
      -> {"type":"qr","url":"...","qr_key":"...","image_base64":"..."}
  auth.py login
      轮询期间每 1.5s 发一次：
        {"type":"status","code":86101}   等待扫码
        {"type":"status","code":86090}   已扫码
      成功 -> {"type":"success","count":N,"refresh_token":"..."}
      失败 -> {"type":"error","message":"..."}
  auth.py check
      -> {"type":"check","state":"valid|invalid|none","message":"..."}
  auth.py delete
      -> {"type":"ok","deleted":bool}

存储（每台机器一份，非 git 追踪）：
  %LocalAppData%/BiliHelper/cookies.json   权威存储 {cookies, refresh_token, ...}
  %LocalAppData%/BiliHelper/cookies.txt    给 yt-dlp 用的 Netscape 格式

依赖：requests, qrcode
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import os
import sys
import time
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import qrcode
import requests

# ---- 目录 / 常量 ----------------------------------------------------
APP_DIR = Path(os.environ.get("LOCALAPPDATA", Path.home())) / "BiliHelper"
COOKIES_JSON = APP_DIR / "cookies.json"
COOKIES_TXT = APP_DIR / "cookies.txt"
FINGERPRINT_FILE = APP_DIR / "fingerprint.json"

QR_GENERATE_URL = "https://passport.bilibili.com/x/passport-login/web/qrcode/generate"
QR_POLL_URL = "https://passport.bilibili.com/x/passport-login/web/qrcode/poll"
FINGER_URL = "https://api.bilibili.com/x/frontend/finger/spi"
COOKIE_INFO_URL = "https://passport.bilibili.com/x/passport-login/web/cookie/info"

USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
)
REFERER = "https://www.bilibili.com/"
POLL_INTERVAL = 1.5
POLL_TIMEOUT = 180.0

WAITING, SCANNED, SUCCESS, EXPIRED = 86101, 86090, 0, 86038


def _emit(data: dict) -> None:
    """输出一行 JSON 到 stdout（WPF 逐行读取）。"""
    print(json.dumps(data, ensure_ascii=False), flush=True)


def _log(msg: str) -> None:
    """输出诊断进度到 stderr（WPF 读 stderr 记入 App.Log）。"""
    print(f"[auth] {msg}", file=sys.stderr, flush=True)


def _session() -> requests.Session:
    s = requests.Session()
    s.headers.update({"User-Agent": USER_AGENT, "Referer": REFERER})
    return s


# finger/spi 返回的字段名是 b_3/b_4(对应 buvid3/buvid4),按 B 站命名写入 cookie。
def _load_fingerprint() -> dict:
    """读本地持久化的设备指纹（跨进程复用，避免每次登录都像新设备）。"""
    try:
        return json.loads(FINGERPRINT_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}


def _save_fingerprint(data: dict) -> None:
    """持久化设备指纹；原子写入，避免并发 / 进程被杀时写坏。"""
    try:
        _atomic_write(FINGERPRINT_FILE, json.dumps(data, ensure_ascii=False))
    except OSError:
        pass


def _apply_fingerprint(
    session: requests.Session, buvid3: str, buvid4: str, source: str
) -> bool:
    """把 buvid3/buvid4 注入 session（空值忽略）。返回是否注入了任一。"""
    if buvid3:
        session.cookies.set("buvid3", buvid3, domain=".bilibili.com")
    if buvid4:
        session.cookies.set("buvid4", buvid4, domain=".bilibili.com")
    if buvid3 or buvid4:
        _log(
            f"设备指纹(来源={source})已生效 buvid3={'有' if buvid3 else '无'} "
            f"buvid4={'有' if buvid4 else '无'}"
        )
        return True
    return False


def _attach_fingerprint(session: requests.Session) -> None:
    """复用本地持久化的设备指纹；没有则拉取一次并落盘。

    多次扫码(尤其短时间内连续 generate)时共享同一设备指纹,降低被风控
    判定为"疑似新设备长时间登录"的概率。失败时静默降级为不注入指纹。
    """
    fp = _load_fingerprint()
    if _apply_fingerprint(
        session, fp.get("buvid3") or "", fp.get("buvid4") or "", "本地"
    ):
        return

    # 本地没有 → 拉取一次并落盘，供后续进程复用
    try:
        r = session.get(FINGER_URL, timeout=10)
        data = r.json().get("data") or {}
        buvid3 = data.get("b_3") or ""
        buvid4 = data.get("b_4") or ""
        if _apply_fingerprint(session, buvid3, buvid4, "拉取"):
            _save_fingerprint({"buvid3": buvid3, "buvid4": buvid4})
    except Exception as e:  # noqa: BLE001
        _log(f"设备指纹拉取失败(不影响登录): {e}")


def load_current() -> dict | None:
    if not COOKIES_JSON.exists():
        return None
    try:
        return json.loads(COOKIES_JSON.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


def _atomic_write(path: Path, content: str) -> None:
    """临时文件 + os.replace 原子替换写入，避免进程被杀时留下半截文件。"""
    APP_DIR.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    tmp.write_text(content, encoding="utf-8")
    os.replace(tmp, path)


def save_current(data: dict) -> None:
    APP_DIR.mkdir(parents=True, exist_ok=True)
    _atomic_write(
        COOKIES_JSON,
        json.dumps(data, ensure_ascii=False, indent=2),
    )
    write_netscape(data.get("cookies", {}), data.get("timestamp", 0))


def write_netscape(cookies: dict, timestamp: int) -> None:
    lines = ["# Netscape HTTP Cookie File", "# generated by bilihelper-auth"]
    for name, value in cookies.items():
        lines.append(f".bilibili.com\tTRUE\t/\tTRUE\t{timestamp}\t{name}\t{value}")
    _atomic_write(COOKIES_TXT, "\n".join(lines) + "\n")


def delete_storage() -> bool:
    removed = False
    for p in (COOKIES_JSON, COOKIES_TXT):
        try:
            if p.exists():
                p.unlink()
                removed = True
        except OSError:
            pass
    return removed


def _build_cookie_header(cookies: dict) -> str:
    return "; ".join(f"{k}={v}" for k, v in cookies.items())


def _extract_cookies(url: str) -> dict[str, str]:
    return {k: v[0] for k, v in parse_qs(urlsplit(url).query).items() if v}


def _extract_from_set_cookie(response: requests.Response) -> dict[str, str]:
    cookies: dict[str, str] = {}
    for header in response.raw.headers.getlist("Set-Cookie"):
        head = header.split(";", 1)[0]
        if "=" in head:
            name, value = head.split("=", 1)
            cookies[name.strip()] = value.strip()
    return cookies


# ---- 二维码流程 -----------------------------------------------------
def qr_generate(session: requests.Session) -> tuple[str, str]:
    payload = session.get(QR_GENERATE_URL, timeout=10).json()
    if payload.get("code") != 0:
        raise RuntimeError(f"二维码生成失败: {payload.get('message')}")
    data = payload.get("data") or {}
    if not data.get("url") or not data.get("qrcode_key"):
        raise RuntimeError("响应缺少 url 或 qrcode_key")
    return data["url"], data["qrcode_key"]


def qr_to_base64(url: str) -> str:
    """生成二维码 PNG 的 base64（内存中，不落盘）。

    避免多个登录子进程并发写同一张 `qr_login.png` 导致图片损坏
    （旧进程未退出时新进程已开始写）。WPF 侧直接解码 base64 显示。
    """
    buf = io.BytesIO()
    qrcode.make(url).save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


def qr_poll(
    session: requests.Session,
    qrcode_key: str,
    interval: float = POLL_INTERVAL,
    timeout: float = POLL_TIMEOUT,
) -> dict:
    url = f"{QR_POLL_URL}?qrcode_key={qrcode_key}"
    start = time.monotonic()
    while True:
        if time.monotonic() - start > timeout:
            raise TimeoutError("二维码已超时，请重新生成")
        response = session.get(url, timeout=10)
        data = response.json().get("data") or {}
        status = int(data.get("code", -1))
        _log(f"poll status={status}")
        if status == SUCCESS:
            if not data.get("url"):
                raise RuntimeError("登录成功但缺少 url")
            # 真实凭证(SESSDATA/bili_jct/DedeUserID...)通过 Set-Cookie 头下发，
            # URL query 里的 ticket/gourl/first_domain 只是跳转参数。
            # 合并响应 Set-Cookie + session 累积的 cookie + URL query。
            cookies: dict[str, str] = {}
            cookies.update(_extract_from_set_cookie(response))
            for name, value in session.cookies.items():
                if name not in cookies and value:
                    cookies[name] = value
            cookies.update(_extract_cookies(data["url"]))
            keys = sorted(cookies.keys())
            _log(f"登录成功, 提取 {len(cookies)} 个 cookie: {keys}")
            if "SESSDATA" not in cookies or "bili_jct" not in cookies:
                _log(f"[WARN] 缺少关键 cookie(SESSDATA/bili_jct), 当前仅有: {keys}")
            _log(f"refresh_token={'有' if data.get('refresh_token') else '无'}")
            return {
                "cookies": cookies,
                "refresh_token": data.get("refresh_token", ""),
                "timestamp": int(data.get("timestamp", 0)),
            }
        if status == EXPIRED:
            raise TimeoutError("二维码已过期，请重新生成")
        _emit({"type": "status", "code": status})
        time.sleep(interval)


# ---- 探测 / 续期 -----------------------------------------------------
def check_status(session: requests.Session, cookies: dict) -> dict:
    if not cookies:
        return {"state": "invalid", "message": "无 cookie"}
    try:
        r = session.get(
            COOKIE_INFO_URL,
            headers={"Cookie": _build_cookie_header(cookies)},
            timeout=10,
        )
        body = r.json()
        code = body.get("code")
        _log(f"cookie/info code={code} message={body.get('message')}")
        if code == 0:
            refresh = bool((body.get("data") or {}).get("refresh"))
            if refresh:
                _log("需要刷新 (refresh=true)")
                return {"state": "invalid", "message": "需要刷新"}
            _log("cookie 有效")
            return {"state": "valid", "message": "有效"}
        if code == -101:
            _log("会话已过期 (code=-101)")
            return {"state": "invalid", "message": "会话已过期"}
        return {"state": "invalid", "message": f"cookie/info: {body.get('message')}"}
    except Exception as e:  # noqa: BLE001
        return {"state": "invalid", "message": f"探测失败: {e}"}


# ---- CLI ------------------------------------------------------------
def cmd_generate(args) -> int:
    session = _session()
    url, key = qr_generate(session)
    _log(f"二维码已生成 qr_key={key}")
    _emit({"type": "qr", "url": url, "qr_key": key, "image_base64": qr_to_base64(url)})
    return 0


def cmd_login(args) -> int:
    session = _session()
    _attach_fingerprint(session)
    url, key = qr_generate(session)
    _log(f"登录流程: 二维码已生成 qr_key={key}")
    _emit({"type": "qr", "url": url, "qr_key": key, "image_base64": qr_to_base64(url)})
    try:
        result = qr_poll(session, key)
    except Exception as e:  # noqa: BLE001
        _log(f"登录失败: {e}")
        _emit({"type": "error", "message": str(e)})
        return 1
    payload = {
        "cookies": result["cookies"],
        "refresh_token": result.get("refresh_token", ""),
        "timestamp": int(time.time() * 1000),
        "source": "qr",
        "login_at": time.time(),
    }
    save_current(payload)
    _log(f"登录成功, 已写入 {COOKIES_JSON} 和 {COOKIES_TXT}")
    _emit(
        {
            "type": "success",
            "count": len(result["cookies"]),
            "refresh_token": result.get("refresh_token", ""),
        }
    )
    return 0


USER_INFO_URL = "https://api.bilibili.com/x/web-interface/nav"


def cmd_user(args) -> int:
    """读取当前登录用户信息（昵称/头像/UID），供 WPF 账号面板展示欢迎卡片。"""
    data = load_current()
    if not data:
        _log("user: 未找到已保存的 cookie")
        _emit({"type": "error", "message": "未登录"})
        return 1
    cookies = data.get("cookies", {})
    if not cookies:
        _emit({"type": "error", "message": "无 cookie"})
        return 1
    try:
        s = _session()
        r = s.get(
            USER_INFO_URL, headers={"Cookie": _build_cookie_header(cookies)}, timeout=10
        )
        body = r.json()
        if body.get("code") != 0:
            _log(
                f"user: 获取用户信息失败 code={body.get('code')} message={body.get('message')}"
            )
            _emit(
                {"type": "error", "message": body.get("message") or "获取用户信息失败"}
            )
            return 1
        data = body.get("data") or {}
        _emit(
            {
                "type": "user",
                "uname": data.get("uname", ""),
                "face": data.get("face", ""),
                "mid": data.get("mid", 0),
            }
        )
        return 0
    except Exception as e:  # noqa: BLE001
        _log(f"user: 请求异常 {e}")
        _emit({"type": "error", "message": str(e)})
        return 1


def cmd_check(args) -> int:
    current = load_current()
    if not current:
        _log("check: 未登录 (无 cookie 记录)")
        _emit({"type": "check", "state": "none", "message": "未登录"})
        return 0
    cookies = current.get("cookies", {})
    if not cookies:
        _emit({"type": "check", "state": "invalid", "message": "无 cookie"})
        return 0
    stat = check_status(_session(), cookies)
    _log(f"check: 状态={stat['state']}")
    if stat["state"] == "valid":
        _emit({"type": "check", "state": "valid", "message": stat["message"]})
        return 0
    # 不再自动续期（refresh 功能已移除）：无效 / 需刷新直接返回，用户重新扫码即可
    _emit({"type": "check", "state": stat["state"], "message": stat["message"]})
    return 0


def cmd_delete(args) -> int:
    deleted = delete_storage()
    _log(f"delete: 已删除 cookie 文件: {deleted}")
    _emit({"type": "ok", "deleted": deleted})
    return 0


def main(argv: list[str]) -> int:
    p = argparse.ArgumentParser(prog="auth")
    sub = p.add_subparsers(dest="cmd", required=True)

    g = sub.add_parser("generate", help="仅生成二维码图片")
    g.set_defaults(func=cmd_generate)

    l = sub.add_parser("login", help="扫码登录")
    l.set_defaults(func=cmd_login)

    c = sub.add_parser("check", help="探测 cookie 状态")
    c.set_defaults(func=cmd_check)

    d = sub.add_parser("delete", help="删除本地 cookie")
    d.set_defaults(func=cmd_delete)

    u = sub.add_parser("user", help="读取当前登录用户信息")
    u.set_defaults(func=cmd_user)

    args = p.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except KeyboardInterrupt:
        _emit({"type": "error", "message": "已取消"})
        raise SystemExit(130)
