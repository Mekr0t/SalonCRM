from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=None, extra="ignore")

    database_url: str = "postgresql+psycopg://salon:salon_ml_dev@localhost:5433/salon_ml"
    main_api_url: str = "http://localhost:5080"
    internal_api_key: str = ""
    cors_origins: str = "http://localhost:5173,http://localhost:8080"
    session_cookie_name: str = "salon_sid"
    model_dir: str = "./models"

    @property
    def cors_origin_list(self) -> list[str]:
        return [origin.strip() for origin in self.cors_origins.split(",") if origin.strip()]


@lru_cache
def get_settings() -> Settings:
    return Settings()
