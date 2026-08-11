"""AiHelper — 基于 OpenAI 兼容接口 (DeepSeek 等) 的 AI 工具集。

用法:
    from AiHelper.reading import AIClient

    client = AIClient.from_env()          # 读取环境变量（WPF 设置窗注入）
    result = client.chat(system, user)    # 返回 JSON 字符串

环境变量配置（由 WPF 设置窗注入子进程，缺失时回退默认值）:
    DEEPSEEK_API_KEY  必填，API Key（未注入时抛 ValueError）
    DEEPSEEK_BASE_URL 可选，默认 https://api.deepseek.com
    DEEPSEEK_MODEL    可选，默认 deepseek-v4-flash
"""

import os

from openai import (
    APIConnectionError,
    APITimeoutError,
    AuthenticationError,
    NotFoundError,
    OpenAI,
    RateLimitError,
)

# 默认配置
_DEFAULT_BASE_URL = "https://api.deepseek.com"
_DEFAULT_MODEL = "deepseek-v4-flash"


class AIClient:
    def __init__(self, api_key: str, base_url: str, model: str):
        # 显式覆盖 SDK 默认值（timeout 600s / max_retries 2）：
        # 默认单请求最坏 3×600s = 30 分钟才报错，UI 会长时间"正在整理中"。
        # 300s 覆盖大分P（P19 实测需 ~210s），最多 2×300s = 10 分钟必出结果。
        self.client = OpenAI(
            api_key=api_key,
            base_url=base_url,
            timeout=300,
            max_retries=1,
        )
        self.model = model

    @classmethod
    def from_env(cls) -> "AIClient":
        """从环境变量构造 AIClient（WPF 设置窗注入 DEEPSEEK_*，缺失时回退默认值）。

        Raises:
            ValueError: 缺少 DEEPSEEK_API_KEY（请在 WPF 设置窗 ⚙️ 中填写）。
        """
        api_key = os.environ.get("DEEPSEEK_API_KEY", "").strip()
        base_url = os.environ.get("DEEPSEEK_BASE_URL", _DEFAULT_BASE_URL).strip()
        model = os.environ.get("DEEPSEEK_MODEL", _DEFAULT_MODEL).strip()

        if not api_key:
            raise ValueError(
                "未找到 DEEPSEEK_API_KEY，请在 WPF 设置窗（⚙️）中填写 API Key"
            )

        return cls(api_key=api_key, base_url=base_url, model=model)

    def chat(self, system: str, user: str):
        """发送单轮对话，强制模型返回 JSON 对象（字符串形式）。

        Args:
            system: 系统提示词（角色定义 + 任务 + 输出格式要求）。
            user: 用户消息内容（视频标题 + 字幕数据）。
        """
        response = self.client.chat.completions.create(
            model=self.model,
            messages=[
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
            response_format={"type": "json_object"},
            # 关闭思考模式（写死）：思维链（reasoning_tokens）是 max_tokens 消耗大头，
            # 思考模式下 P37 需 37472 tokens 会被截断；关闭后实测仅需 ~11k tokens，
            # 32768 余量约 3 倍，足够覆盖所有分P。调大 65536 反而触发 opencode 代理 500（请求超载）。
            max_tokens=32768,
            extra_body={"thinking": {"type": "disabled"}},
        )

        return response.choices[0].message.content

    def test_connectivity(self) -> tuple[bool, str]:
        """轻量连通性测试：发起一次极小请求，不返回正文。

        用于 WPF 设置页「测试连通性」按钮。区分三类失败：

        Returns:
            (True, "连接成功") 成功；
            (False, 分类提示信息) 失败。
        """
        try:
            self.client.chat.completions.create(
                model=self.model,
                messages=[{"role": "user", "content": "hi"}],
                max_tokens=1,
            )
        except AuthenticationError:
            return False, "API Key 无效或已过期（认证失败）"
        except NotFoundError:
            return False, f"模型不存在：{self.model}（请检查模型名或 base_url）"
        except RateLimitError:
            return False, "触发限流（额度不足或请求过频）"
        except APITimeoutError:
            return False, "连接超时，请检查网络"
        except APIConnectionError:
            return False, "无法连接到 base_url，请检查网络或地址"
        except Exception as e:  # noqa: BLE001 — 未知错误兜底
            return False, f"连接失败（未知错误）：{e}"
        return True, "连接成功"
