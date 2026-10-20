import json
import uuid

import httpx
import pytest

from app.clients.main_api import MainApiClient, MainApiUnavailable

USER_ID = str(uuid.uuid4())


def make_client(handler) -> MainApiClient:
    return MainApiClient("http://main", "secret-key", transport=httpx.MockTransport(handler))


def test_introspect_sends_key_and_body_and_maps_active() -> None:
    seen: list[httpx.Request] = []

    def handler(request: httpx.Request) -> httpx.Response:
        seen.append(request)
        return httpx.Response(
            200, json={"active": True, "userId": USER_ID, "role": "Admin", "fullName": "Eva"}
        )

    result = make_client(handler).introspect("tok")

    assert result is not None
    assert (str(result.user_id), result.role, result.full_name) == (USER_ID, "Admin", "Eva")
    request = seen[0]
    assert request.method == "POST"
    assert str(request.url) == "http://main/api/internal/sessions/introspect"
    assert request.headers["X-Internal-Api-Key"] == "secret-key"
    assert json.loads(request.content) == {"sessionToken": "tok"}


def test_introspect_inactive_returns_none() -> None:
    client = make_client(lambda r: httpx.Response(200, json={"active": False}))
    assert client.introspect("tok") is None


def test_fetch_export_returns_json_and_sends_key() -> None:
    def handler(request: httpx.Request) -> httpx.Response:
        assert request.method == "GET"
        assert request.url.path == "/api/internal/ml/export"
        assert request.headers["X-Internal-Api-Key"] == "secret-key"
        return httpx.Response(200, json={"customers": [], "appointments": []})

    assert make_client(handler).fetch_export() == {"customers": [], "appointments": []}


def _raise(exc: Exception):
    def handler(request: httpx.Request) -> httpx.Response:
        raise exc

    return handler


@pytest.mark.parametrize(
    "handler",
    [
        _raise(httpx.ConnectTimeout("t")),
        _raise(httpx.ReadTimeout("t")),
        _raise(httpx.ConnectError("c")),
        lambda r: httpx.Response(500),
        lambda r: httpx.Response(401),
        lambda r: httpx.Response(200, content=b"not json"),
        lambda r: httpx.Response(200, json=[1, 2]),
        lambda r: httpx.Response(200, json={"active": True, "role": "Root"}),
        lambda r: httpx.Response(200, json={"foo": "bar"}),
    ],
)
def test_failures_raise_main_api_unavailable(handler) -> None:
    with pytest.raises(MainApiUnavailable):
        make_client(handler).introspect("tok")


def test_fetch_export_failure_raises() -> None:
    with pytest.raises(MainApiUnavailable):
        make_client(lambda r: httpx.Response(401)).fetch_export()


def test_key_and_token_are_not_logged(caplog: pytest.LogCaptureFixture) -> None:
    with pytest.raises(MainApiUnavailable):
        make_client(lambda r: httpx.Response(401)).introspect("my-session-token")
    assert "secret-key" not in caplog.text
    assert "my-session-token" not in caplog.text
