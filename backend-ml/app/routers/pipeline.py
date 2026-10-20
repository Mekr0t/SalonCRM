from datetime import datetime

from fastapi import APIRouter, Depends

from app.auth import CurrentUser, require_role
from app.clients.main_api import MainApiClient, get_main_api
from app.config import get_settings
from app.pipeline import PipelineResult, run_pipeline, utc_now

router = APIRouter(prefix="/ml", tags=["pipeline"])

admin_only = require_role("Admin")


@router.post(
    "/pipeline/run",
    response_model=PipelineResult,
    response_model_by_alias=True,
    summary="Sync, train and predict (SPEC 8.3)",
    responses={
        409: {"description": "Pipeline already running"},
        422: {"description": "Not enough history to train"},
        502: {"description": "Malformed export from main"},
        503: {"description": "Main backend unavailable"},
    },
)
def run(
    _: CurrentUser = Depends(admin_only),
    main_api: MainApiClient = Depends(get_main_api),
    now: datetime = Depends(utc_now),
) -> PipelineResult:
    return run_pipeline(main_api, get_settings(), now)
