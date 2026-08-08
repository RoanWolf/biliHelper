# AGENTS.md

Bilibili subtitle extraction + desktop viewer + AI polish. **Python backend is a pure data pipeline; WPF (.NET 10) owns UI, state, and local persistence.** README.md is thorough and authoritative — read it before deep changes.

## Layout
- `BiliHelperCore/` — Python backend (`uv` project, requires-python >=3.13). No framework.
  - `main.py` (CLI, --stream JSONL), `bili_helper.py` (yt-dlp + SRT), `analyze_sub.py` (**standalone diagnostic tool, not part of the WPF pipeline**), `auth.py` (B站扫码登录/续期/探测, stdout JSONL), `AiHelper/` (`ai_read.py` per-part AI polish; `reading.py` = AIClient + custom `.env` loader)
- `BiliHelperWpf/` — .NET WPF, `net10.0-windows`, **zero NuGet packages** (pure native). `Services/` spawn Python and read pipes; `ViewModels/MainViewModel.cs` is the state/concurrency hub.
  - `Themes/` (`Light.xaml`, `Dark.xaml` = 34 same-key brushes, `ScrollBar.xaml`), `ThemeManager.cs` (runtime swap + persistence), `App.xaml.cs` loads persisted theme on startup.

## Commands
```sh
# Python deps (in BiliHelperCore)
uv sync

# Build WPF (in BiliHelperWpf)
dotnet build
```
No test project and no linter config exist — use `dotnet build` / `uv` for verification.

## Architecture — do not break these contracts
- Cross-process transport is **stdout JSONL** (one JSON object per line) from Python to WPF. Python must `print(json.dumps(...), flush=True)`. WPF reads each line and dispatches via `StreamEvent` / `AiReadMeta`.
- Both Python calls (`BiliService.cs:47`, `AiReadService.cs:46`) launch a subprocess at **`BiliHelperCore/.venv/Scripts/python.exe` directly — never via `uv run`** (avoids uv env discovery; consistent with how `bili_helper.py` invokes yt-dlp).
- Must set `PYTHONIOENCODING=utf-8` on subprocess and `Encoding.UTF8` on both redirect streams.
- Cancellation must kill the whole process tree (`process.Kill(entireProcessTree: true)`).

## Paths / repo-relative gotchas
- Python and cookie paths are resolved by walking up from the app base directory until a folder named **`bilihelperCore`** is found (`FindProjectRoot` / `FindDirContaining`). Works because Windows is case-insensitive; keep the relative folder layout when moving output.
- Cookies: B站登录态不再硬编码。**`auth.py` 扫码登录后写到 `%LocalAppData%\BiliHelper\cookies.json`(权威) + `cookies.txt`(Netscape, 供 yt-dlp)**. `BiliService.cs` 的 cookie 路径指向该 LocalAppData `cookies.txt`. 拔掉扫码登录就无 cookie → 需扫码. 旧文件 `www.bilibili.com_cookies.txt` 已被新流程取代.
- B站登录可用 `refresh_cookie.py` 逻辑续期(约1月过期)，WPF 启动时 `auth.py check --refresh` 自动续期.
- API key: copy `BiliHelperCore/.env.example` → `.env`. `.env` is gitignored. `AIClient.load_env_file` is a hand-rolled parser (no python-dotenv).
- Persistence (WPF only, no DB): `BiliHelperWpf/history/YYYYMMDD/BVxxxx/raw.json` (full video) and `read.json` (`parts[]`, incremental per-part, lock-protected concurrent writes).

## AI polish conventions
- One DeepSeek call per part (`response_format=json_object`), parse failure retries once; default model is hardcoded `deepseek-v4-flash` in `reading.py`.
- `ai_read.py` reads `raw.json` with `encoding="utf-8-sig"` (BOM-tolerant) and clamps subtitle indices to `1..subtitle_count`.
- Cancellation + per-part independent `CancellationTokenSource` → concurrent polish of multiple parts.

## Theming contracts (do not break)
- `Light.xaml` / `Dark.xaml` must define **the same brush keys** (34 each) — swapping the dictionary refreshes all `DynamicResource`. `ThemeManager` locates the theme dictionary by regex `Light\.xaml$|Dark\.xaml$` (never the shared `ScrollBar.xaml`).
- MainWindow colors must use **`DynamicResource`** so they follow runtime theme swaps.
- **`Binding.Converter` is not a dependency property** — it cannot use `DynamicResource`. Converter references (`BoolToVis`, `InverseBoolToVis`, `NullToVis`, `StatusToColor`) must stay `StaticResource` (defined in `Window.Resources`). `StatusToColorConverter` reads brushes at runtime via `Application.Current.TryFindResource` so it still follows the theme.
- In `ScrollBar.xaml`, any referenced `x:Key` style must be declared **before** the implicit style (`StaticResource` resolves forward only).
- Theme persisted to `%LocalAppData%\BiliHelper\theme.txt`; `App.xaml.cs` calls `ThemeManager.LoadPersisted()` on startup.

## Ignored / unrelated
- `notes/` contains personal OS/study notes — not part of the codebase. `_log/`, `history/`, `.env`, cookies are gitignored.
- WPF 侧登录流程: `AuthService.cs` spawn 调 `auth.py`; `LoginWindow.xaml` 独立扫码窗; 主界面 `MainWindow.xaml` cookie 按钮 🍪(有效)/⚠(无效, 点击弹扫码) 由 `MainViewModel.CookieState` 驱动; 启动 `OnContentRendered` 探测, 无 cookie 首次自动弹窗.