from pydantic import BaseModel, ConfigDict
from pydantic.alias_generators import to_camel


class CamelModel(BaseModel):
    """Base for all request/response schemas: camelCase JSON, snake_case in Python."""

    model_config = ConfigDict(alias_generator=to_camel, populate_by_name=True)
