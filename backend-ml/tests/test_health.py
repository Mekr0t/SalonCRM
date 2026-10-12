from fastapi.testclient import TestClient


def test_health_returns_status_keys(client: TestClient) -> None:
    response = client.get("/ml/health")
    assert response.status_code == 200
    body = response.json()
    assert "status" in body
    assert "database" in body
    assert body["status"] in {"ok", "degraded"}


def test_unknown_path_returns_problem_json(client: TestClient) -> None:
    response = client.get("/ml/does-not-exist")
    assert response.status_code == 404
    assert response.headers["content-type"].startswith("application/problem+json")
    body = response.json()
    assert body["status"] == 404
    assert {"type", "title", "status", "detail"} <= body.keys()


def test_cors_preflight_allows_credentials_for_frontend_origin(client: TestClient) -> None:
    response = client.options(
        "/ml/health",
        headers={
            "Origin": "http://localhost:5173",
            "Access-Control-Request-Method": "GET",
        },
    )
    assert response.headers["access-control-allow-credentials"] == "true"
    assert response.headers["access-control-allow-origin"] == "http://localhost:5173"
