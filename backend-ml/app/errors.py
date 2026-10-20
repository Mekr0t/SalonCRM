from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

PROBLEM_JSON = "application/problem+json"

_TITLES = {
    400: "Bad Request",
    401: "Unauthorized",
    403: "Forbidden",
    404: "Not Found",
    405: "Method Not Allowed",
    409: "Conflict",
    422: "Validation Failed",
    500: "Internal Server Error",
    503: "Service Unavailable",
}


def problem_response(status: int, detail: str | None, title: str | None = None) -> JSONResponse:
    body = {
        "type": f"https://httpstatuses.com/{status}",
        "title": title or _TITLES.get(status, "Error"),
        "status": status,
        "detail": detail,
    }
    return JSONResponse(status_code=status, content=body, media_type=PROBLEM_JSON)


async def http_exception_handler(_: Request, exc: StarletteHTTPException) -> JSONResponse:
    return problem_response(exc.status_code, str(exc.detail) if exc.detail is not None else None)


async def validation_exception_handler(_: Request, exc: RequestValidationError) -> JSONResponse:
    errors = exc.errors()
    detail = "; ".join(
        f"{'.'.join(str(part) for part in err.get('loc', ()))}: {err.get('msg', '')}"
        for err in errors
    )
    return problem_response(422, detail or "Validation failed", title=_TITLES[422])


def register_error_handlers(app: FastAPI) -> None:
    app.add_exception_handler(StarletteHTTPException, http_exception_handler)
    app.add_exception_handler(RequestValidationError, validation_exception_handler)
