import hashlib
import uuid
from collections.abc import Iterator

import httpx
import pytest
from fastapi import Depends, FastAPI
from fastapi.testclient import TestClient

from app import auth
from app.auth import CurrentUser, current_user, require_role
from app.clients.main_api import MainApiClient, get_main_api
from app.errors import register_error_handlers

ADMIN = {"active": True, "userId": str(uuid.uuid4()), "role": "Admin", "fullName": "Eva Admin"}
USER = {"active": True, "userId": str(uuid.uuid4()), "role": "User", "fullName": "Uma User"}

admin_only = require_role("Admin")


class FakeMain:
    """Programmable main backend behind httpx.MockTransport."""

    def __init__(self) -> None:
        self.calls = 0
        self.response: httpx.Response | Exception = httpx.Response(200, json=ADMIN)

    def __call__(self, request: httpx.Request) -> httpx.Response:
        self.calls += 1
        if isinstance(self.response, Exception):
            raise self.response
        return self.response


@pytest.fixture
def main() -> FakeMain:
    return FakeMain()


@pytest.fixture
def api(main: FakeMain) -> Iterator[TestClient]:
    app = FastAPI()
    register_error_handlers(app)

    @app.get("/me")
    def me(user: CurrentUser = Depends(current_user)) -> dict[str, str]:
        return {"name": user.full_name, "role": user.role}

    @app.get("/admin")
    def admin(user: CurrentUser = Depends(admin_only)) -> dict[str, str]:
        return {"name": user.full_name}

    client = MainApiClient("http://main", "k", transport=httpx.MockTransport(main))
    app.dependency_overrides[get_main_api] = lambda: client
    auth.clear_auth_cache()
    yield TestClient(app)
    auth.clear_auth_cache()


def get(api: TestClient, path: str, token: str | None = "tok") -> httpx.Response:
    api.cookies.clear()
    if token is not None:
        api.cookies.set("salon_sid", token)
    return api.get(path)


def test_missing_cookie_is_401_problem_json(api: TestClient, main: FakeMain) -> None:
    response = get(api, "/me", token=None)
    assert response.status_code == 401
    assert response.headers["content-type"].startswith("application/problem+json")
    assert response.json()["status"] == 401
    assert main.calls == 0


def test_empty_cookie_is_401(api: TestClient) -> None:
    assert get(api, "/me", token="").status_code == 401


def test_inactive_session_is_401(api: TestClient, main: FakeMain) -> None:
    main.response = httpx.Response(200, json={"active": False})
    assert get(api, "/me").status_code == 401


def test_main_down_is_503_problem_json(api: TestClient, main: FakeMain) -> None:
    main.response = httpx.ConnectError("down")
    response = get(api, "/me")
    assert response.status_code == 503
    assert response.json()["title"] == "Service Unavailable"
    assert response.headers["content-type"].startswith("application/problem+json")


def test_main_rejecting_key_is_503(api: TestClient, main: FakeMain) -> None:
    main.response = httpx.Response(401)
    assert get(api, "/me").status_code == 503


def test_user_on_admin_route_is_403(api: TestClient, main: FakeMain) -> None:
    main.response = httpx.Response(200, json=USER)
    response = get(api, "/admin")
    assert response.status_code == 403
    assert response.json()["title"] == "Forbidden"


def test_admin_on_admin_route_is_200(api: TestClient) -> None:
    response = get(api, "/admin")
    assert response.status_code == 200
    assert response.json() == {"name": "Eva Admin"}


def test_cache_hits_within_ttl_and_expires(
    api: TestClient, main: FakeMain, monkeypatch: pytest.MonkeyPatch
) -> None:
    now = [1000.0]
    monkeypatch.setattr(auth.time, "monotonic", lambda: now[0])

    assert get(api, "/me").status_code == 200
    now[0] += 29
    assert get(api, "/me").status_code == 200
    assert main.calls == 1

    now[0] += 2  # 31 s after the first lookup
    assert get(api, "/me").status_code == 200
    assert main.calls == 2


def test_inactive_results_are_not_cached(api: TestClient, main: FakeMain) -> None:
    main.response = httpx.Response(200, json={"active": False})
    get(api, "/me")
    get(api, "/me")
    assert main.calls == 2


def test_cache_keys_are_hashed_and_expired_entries_evicted(
    api: TestClient, monkeypatch: pytest.MonkeyPatch
) -> None:
    now = [0.0]
    monkeypatch.setattr(auth.time, "monotonic", lambda: now[0])
    get(api, "/me", token="secret-token")
    assert list(auth._cache) == [hashlib.sha256(b"secret-token").hexdigest()]

    now[0] += 31
    get(api, "/me", token="other-token")
    assert list(auth._cache) == [hashlib.sha256(b"other-token").hexdigest()]
