"""Client for the main backend's internal API (SPEC §7)."""

import logging
import uuid
from typing import Any, Literal

import httpx
from pydantic import BaseModel, ValidationError

from app.config import get_settings

logger = logging.getLogger(__name__)

INTROSPECT_TIMEOUT = httpx.Timeout(3.0)
# The export is much larger than an introspection reply, so reading may take longer.
EXPORT_TIMEOUT = httpx.Timeout(30.0, connect=3.0)


class MainApiUnavailable(Exception):
    """The main backend could not be reached or answered with an unusable response."""


class IntrospectResult(BaseModel):
    user_id: uuid.UUID
    role: Literal["Admin", "User"]
    full_name: str


class MainApiClient:
    def __init__(
        self,
        base_url: str,
        api_key: str,
        transport: httpx.BaseTransport | None = None,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._headers = {"X-Internal-Api-Key": api_key}
        self._transport = transport

    def _request(
        self, method: str, path: str, timeout: httpx.Timeout, **kwargs: Any
    ) -> dict[str, Any]:
        try:
            with httpx.Client(
                base_url=self._base_url,
                headers=self._headers,
                timeout=timeout,
                transport=self._transport,
            ) as http:
                response = http.request(method, path, **kwargs)
            response.raise_for_status()
            body = response.json()
        except httpx.HTTPStatusError as exc:
            status = exc.response.status_code
            logger.error("Main API %s %s returned HTTP %s", method, path, status)
            raise MainApiUnavailable(f"main API returned HTTP {status}") from exc
        except httpx.HTTPError as exc:
            logger.error("Main API %s %s failed: %s", method, path, type(exc).__name__)
            raise MainApiUnavailable("main API is unreachable") from exc
        except ValueError as exc:
            logger.error("Main API %s %s returned invalid JSON", method, path)
            raise MainApiUnavailable("main API returned invalid JSON") from exc
        if not isinstance(body, dict):
            logger.error("Main API %s %s returned unexpected JSON shape", method, path)
            raise MainApiUnavailable("main API returned unexpected body")
        return body

    def introspect(self, session_token: str) -> IntrospectResult | None:
        """Resolve a session token; None when the session is not active."""
        body = self._request(
            "POST",
            "/api/internal/sessions/introspect",
            INTROSPECT_TIMEOUT,
            json={"sessionToken": session_token},
        )
        if body.get("active") is False:
            return None
        if body.get("active") is not True:
            raise MainApiUnavailable("main API returned malformed introspection")
        try:
            return IntrospectResult(
                user_id=body.get("userId"),
                role=body.get("role"),
                full_name=body.get("fullName"),
            )
        except ValidationError as exc:
            logger.error("Main API introspection had invalid fields")
            raise MainApiUnavailable("main API returned malformed introspection") from exc

    def fetch_export(self) -> dict[str, Any]:
        """Download the pseudonymized customers and appointments export."""
        return self._request("GET", "/api/internal/ml/export", EXPORT_TIMEOUT)


def get_main_api() -> MainApiClient:
    """FastAPI dependency; tests override it with a client on httpx.MockTransport."""
    settings = get_settings()
    return MainApiClient(settings.main_api_url, settings.internal_api_key)
