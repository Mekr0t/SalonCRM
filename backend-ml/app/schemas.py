from pydantic import BaseModel, ConfigDict
from pydantic.alias_generators import to_camel


class CamelModel(BaseModel):
    """Base for all request/response schemas: camelCase JSON, snake_case in Python."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)


class MetricsOut(CamelModel):
    """Evaluation metrics of one model or the baseline (SPEC 8.3 step 7)."""

    roc_auc: float | None
    precision: float
    recall: float
    f1: float
    confusion_matrix: list[list[int]]
    positive_rate: float
    n: int


class CandidateOut(CamelModel):
    algorithm: str
    metrics: MetricsOut


def candidates_out(candidates: dict | list) -> list[CandidateOut]:
    """Stored candidates ({algorithm: metrics}) as an API list."""
    if isinstance(candidates, dict):
        return [CandidateOut(algorithm=a, metrics=MetricsOut(**m)) for a, m in candidates.items()]
    return [CandidateOut.model_validate(c) for c in candidates]
