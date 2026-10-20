"""Authentication and authorization via main-backend sessions (SPEC §8.6)."""

import hashlib
import threading
import time
import uuid
from collections.abc import Callable
from dataclasses import dataclass
from typing import Literal

from fastapi import Depends, HTTPException, Request

from app.clients.main_api import IntrospectResult, MainApiClient, MainApiUnavailable, get_main_api
from app.config import get_settings

CACHE_TTL_SECONDS = 30.0

Role = Literal["Admin", "User"]


@dataclass(frozen=True)
class CurrentUser:
    user_id: uuid.UUID
    role: Role
    full_name: str


_cache: dict[str, tuple[float, CurrentUser]] = {}
_cache_lock = threading.Lock()


def clear_auth_cache() -> None:
    with _cache_lock:
        _cache.clear()


def _cache_get(key: str) -> CurrentUser | None:
    now = time.monotonic()
    with _cache_lock:
        for stale in [k for k, (expires, _) in _cache.items() if expires <= now]:
            del _cache[stale]
        entry = _cache.get(key)
    return entry[1] if entry else None


def _cache_put(key: str, user: CurrentUser) -> None:
    with _cache_lock:
        _cache[key] = (time.monotonic() + CACHE_TTL_SECONDS, user)


def _to_user(result: IntrospectResult) -> CurrentUser:
    return CurrentUser(user_id=result.user_id, role=result.role, full_name=result.full_name)


def current_user(request: Request, main_api: MainApiClient = Depends(get_main_api)) -> CurrentUser:
    token = request.cookies.get(get_settings().session_cookie_name)
    if not token:
        raise HTTPException(status_code=401, detail="Authentication required.")
    # Only the hash of the token is kept in memory.
    key = hashlib.sha256(token.encode()).hexdigest()
    cached = _cache_get(key)
    if cached is not None:
        return cached
    try:
        result = main_api.introspect(token)
    except MainApiUnavailable as exc:
        raise HTTPException(status_code=503, detail="Authentication service unavailable.") from exc
    if result is None:
        raise HTTPException(status_code=401, detail="Session is not active.")
    user = _to_user(result)
    _cache_put(key, user)
    return user


def require_role(*roles: Role) -> Callable[..., CurrentUser]:
    """Dependency factory: the current user must have one of the given roles."""

    def dependency(user: CurrentUser = Depends(current_user)) -> CurrentUser:
        if user.role not in roles:
            raise HTTPException(status_code=403, detail="Insufficient role.")
        return user

    return dependency
