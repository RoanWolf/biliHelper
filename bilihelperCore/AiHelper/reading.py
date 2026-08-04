"""AiHelper — 基于 DeepSeek (OpenAI 兼容接口) 的 AI 工具集。

用法:
    from AiHelper.reading import AIClient

    client = AIClient.from_env()          # 自动读取 BiliHelperCore/.env
    result = client.chat(prompt)          # 返回 JSON 字符串

.env 配置项:
    DEEPSEEK_API_KEY  必填，DeepSeek API Key
    DEEPSEEK_BASE_URL 可选，默认 https://api.deepseek.com
    DEEPSEEK_MODEL    可选，默认 deepseek-v4-flash
"""

import os
from pathlib import Path

from openai import OpenAI

# 默认配置
_DEFAULT_BASE_URL = "https://api.deepseek.com"
_DEFAULT_MODEL = "deepseek-v4-flash"


def load_env_file(
    env_path: str | Path | None = None,
    overwrite: bool = False,
) -> Path | None:
    """加载 .env 文件到 os.environ。

    轻量级 .env 解析器（不依赖 python-dotenv）：
      - 每行 ``KEY=VALUE``，支持 ``#`` 行注释
      - 值支持可选的双引号/单引号包裹
      - 默认不覆盖已存在的环境变量（overwrite=True 时强制覆盖）

    Args:
        env_path: 显式指定 .env 文件路径；为 None 时自动查找：
                  1. BiliHelperCore/.env（当前文件上一级，即 Python 包根目录）
                  2. AiHelper/.env（当前文件同目录）
                  3. 工作目录 .env

    Returns:
        成功加载的 .env 路径；未找到文件返回 None。
    """
    if env_path is None:
        here = Path(__file__).resolve().parent  # .../BiliHelperCore/AiHelper
        candidates = [
            here.parent / ".env",  # .../BiliHelperCore/.env
            here / ".env",  # .../BiliHelperCore/AiHelper/.env
            Path.cwd() / ".env",  # 工作目录 .env
        ]
        for cand in candidates:
            if cand.is_file():
                env_path = cand
                break
        else:
            return None

    env_path = Path(env_path)
    if not env_path.is_file():
        return None

    for raw_line in env_path.read_text(encoding="utf-8").splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#"):
            continue
        if "=" not in line:
            continue

        key, _, value = line.partition("=")
        key = key.strip()
        value = value.strip()

        # 去除可选的包裹引号（单引号或双引号）
        if len(value) >= 2 and value[0] == value[-1] and value[0] in ('"', "'"):
            value = value[1:-1]

        if not key:
            continue
        if not overwrite and key in os.environ:
            continue
        os.environ[key] = value

    return env_path


class AIClient:
    def __init__(self, api_key: str, base_url: str, model: str):
        self.client = OpenAI(api_key=api_key, base_url=base_url)
        self.model = model

    @classmethod
    def from_env(
        cls,
        env_path: str | Path | None = None,
    ) -> "AIClient":
        """从环境变量构造 AIClient。

        自动加载 .env 文件（查找顺序见 load_env_file），
        然后读取 DEEPSEEK_API_KEY / DEEPSEEK_BASE_URL / DEEPSEEK_MODEL。

        Args:
            env_path: 显式指定 .env 文件路径；None 时自动查找。

        Raises:
            ValueError: 缺少 DEEPSEEK_API_KEY。
        """
        load_env_file(env_path)

        api_key = os.environ.get("DEEPSEEK_API_KEY", "").strip()
        base_url = os.environ.get("DEEPSEEK_BASE_URL", _DEFAULT_BASE_URL).strip()
        model = os.environ.get("DEEPSEEK_MODEL", _DEFAULT_MODEL).strip()

        if not api_key:
            raise ValueError("未找到 DEEPSEEK_API_KEY，请检查 BiliHelperCore/.env 文件")

        return cls(api_key=api_key, base_url=base_url, model=model)

    def chat(self, prompt: str):
        """发送单轮对话，强制模型返回 JSON 对象（字符串形式）。"""
        response = self.client.chat.completions.create(
            model=self.model,
            messages=[{"role": "user", "content": prompt}],
            response_format={"type": "json_object"},
        )

        return response.choices[0].message.content
