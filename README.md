# BiliHelper

> B 站字幕提取 + AI 润色 + 飞书同步的 Windows 桌面工具。

粘贴一个 B 站视频链接，一键拉取全部分P 字幕；接上 DeepSeek，把零散的 AI 字幕整理成流畅的阅读版全文；还能自动同步到飞书云文档，链接直达你的群聊。

---

## ✨ 特性

- 🎬 **一键拉取**：粘贴视频链接（BV/AV/完整 URL），自动获取全部分P 字幕，边拉边落盘，大视频也不怕中断
- 📱 **扫码登录**：B 站官方扫码登录，cookie 只存本机（`%LocalAppData%\BiliHelper`），设备指纹持久化降低风控
- 🤖 **AI 润色**：接入 DeepSeek（OpenAI 兼容接口），修正错别字、补全标点、合并分段，支持多分P 并发整理
- 📄 **飞书同步**：整理完成后自动生成飞书云文档（1 分P = 1 篇文档），机器人往群发链接；重复同步覆盖更新，旧链接永远有效
- 📚 **历史管理**：本地 JSON 存储（按日期/BV 组织），历史抽屉 + 全文搜索（200ms 防抖，数万条字幕不卡顿）
- 🎨 **深浅主题**：运行时一键切换，偏好持久化

---

## 🚀 下载安装

1. 打开 [Releases](https://github.com/RoanWolf/biliHelper/releases) 页面，下载最新的 `BiliHelper-Setup-vX.Y.Z.exe`
2. 双击运行安装向导（**用户级安装，无需管理员权限**，默认装到 `%LocalAppData%\Programs\BiliHelper`）
3. 从开始菜单或桌面快捷方式启动

**系统要求**：Windows 10 / 11（x64）。应用自包含 .NET 10 运行时与 Python 3.13 运行时，**无需预装任何环境**。

> ⚠️ 安装包未做数字签名，SmartScreen 可能提示"已保护你的电脑"——点「更多信息」→「仍要运行」即可。个别杀软对打包的 Python 解释器可能误报，添加信任即可。

---

## 📖 快速上手

### 1. 拉取字幕

复制 B 站视频链接（视频页 URL / BV 号 / AV 号均可），粘贴到主窗口输入框，点「拉取字幕」。进度实时显示，全部完成后即可在「原始字幕」页浏览、搜索。

> 未登录也可拉取公开视频字幕；登录后支持大会员专享等需要登录态的内容。

### 2. 登录 B 站（可选）

首次启动若未登录会自动弹出设置中心账号面板，手机 B 站 App 扫码即可。登录态约 1 个月有效，过期后重新扫码。点击「退出登录」即清除本地 cookie。

### 3. AI 润色（需要 DeepSeek API Key）

1. 到 [DeepSeek 开放平台](https://platform.deepseek.com) 注册并创建 API Key
2. 打开设置中心（主窗口 ⚙️）→「AI 模型」面板，填入 API Key（Base URL 与模型名留空即用官方默认 `https://api.deepseek.com` / `deepseek-v4-flash`），点「测试连通性」验证
3. 切到「AI 润色」页，点「整理当前分P」，等待生成阅读版全文；多个分P 可并发整理

> Base URL 不要以 `/chat/completions` 结尾；模型名不要带 provider 前缀。配置保存在 `%LocalAppData%\BiliHelper\ai_settings.json`（仅本机）。

### 4. 飞书同步（可选）

设置中心 →「飞书」面板：填入自建应用的 App ID / App Secret、目标群号（`oc_` 开头）、根文件夹名，点「测试」往群里发一条测试消息验证。启用后，每个分P 在 AI 整理成功后会自动同步为飞书云文档并把链接发到群里。

> 需要先在[飞书开放平台](https://open.feishu.cn)创建企业自建应用，开通云文档 `drive:drive` 等权限，并**发布版本**后才会生效。

---

## ❓ 常见问题

**Q: 提示未登录 / 字幕拉取失败？**
先用浏览器确认视频本身有字幕（AI 字幕或 CC 字幕）。部分视频无字幕属正常；cookie 过期（约 1 月）重新扫码即可。

**Q: AI 整理报错？**
设置中心「测试连通性」会分类提示：API Key 无效（认证失败）、模型不存在（检查模型名/Base URL）、额度不足（限流）、网络不通。网络类错误会自动重试 2 次。

**Q: 字幕接口变化导致拉取失败？**
后端依赖 yt-dlp，B 站接口调整时到 Releases 页更新到最新版本即可。

**Q: 数据存在哪里？**
历史记录存于安装目录 `history/`（按日期/BV 分目录）；cookie、AI 设置、飞书设置、主题等存于 `%LocalAppData%\BiliHelper\`。卸载时勾选删除即可一并清除。

---

## 🔧 开发者构建

```shell
# Python 后端（需 Python ≥ 3.13 + uv）
cd BiliHelperCore
uv sync

# WPF 桌面端（需 .NET 10 SDK）
cd ../BiliHelperWpf
dotnet build
```

运行：`dotnet run --project BiliHelperWpf`（开发布局下 WPF 会自动向上定位 `bilihelperCore` 目录并调用其中的 `.venv\Scripts\python.exe`）。

### 发布新版本

打 tag 即触发 GitHub Actions 云端构建（publish → 组装 Python 运行时 → Inno Setup 打包 → 自动创建 Release）：

```shell
git tag v1.1.0
git push origin v1.1.0
```

## 🏗️ 架构速览

- **Python 后端是纯数据管道**：字幕抓取（yt-dlp + SRT 解析）、B 站扫码登录、AI 润色（DeepSeek）、飞书同步四个入口，各自是独立子进程脚本，通过 **stdout JSONL** 与桌面端通信（每行一个 JSON，`flush=True` 实时推送）
- **WPF 桌面端（.NET 10 + WPF-UI 4.3）拥有 UI、状态与本地持久化**：流式管道读取、分P 懒加载（`index.json` + `parts/NNN.json`）、AI 结果增量写 `read.json`（锁保护并发）
- 本地存储：文件夹即索引，无数据库——`history/YYYYMMDD/BVxxx/{index.json, parts/, read.json}`

---

## ⚠️ 免责声明

本项目仅供个人学习与技术交流使用。字幕版权归原作者及 B 站所有，请勿将抓取内容用于商业用途或二次传播；AI 润色与飞书同步依赖第三方服务，请遵守相应服务条款。

---

## 📄 License

© 2026 [RoanWolf](https://github.com/RoanWolf)
