from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware

from app.config import get_settings
from app.errors import register_error_handlers
from app.routers import health, model, pipeline, predictions, stats

settings = get_settings()

app = FastAPI(title="SalonCRM ML", docs_url="/docs")

# Explicit origins only: allow_credentials=True is incompatible with "*".
app.add_middleware(
    CORSMiddleware,
    allow_origins=settings.cors_origin_list,
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

register_error_handlers(app)
app.include_router(health.router)
app.include_router(pipeline.router)
app.include_router(predictions.router)
app.include_router(model.router)
app.include_router(stats.router)
