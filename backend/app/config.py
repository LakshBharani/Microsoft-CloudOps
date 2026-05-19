from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=".env", env_file_encoding="utf-8", extra="ignore")

    azure_ai_project_connection_string: str = ""
    azure_ai_agent_model: str = "gpt-5.1"
    azure_subscription_id: str = ""


@lru_cache
def get_settings() -> Settings:
    return Settings()
