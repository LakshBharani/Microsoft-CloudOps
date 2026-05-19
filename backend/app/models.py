from __future__ import annotations

import json
from typing import Any

from pydantic import BaseModel, Field, field_validator


class ResourceNode(BaseModel):
    id: str = ""
    name: str = ""
    type: str = ""
    location: str = ""
    resourceGroup: str = ""
    tags: dict[str, str] = Field(default_factory=dict)
    properties: dict[str, Any] = Field(default_factory=dict)
    skuJson: str | None = None
    kind: str | None = None


class DesiredResourceNode(BaseModel):
    name: str = ""
    type: str = ""
    resourceGroup: str = ""
    location: str = ""
    skuJson: str | None = None
    kind: str | None = None
    tags: dict[str, str] = Field(default_factory=dict)

    @field_validator("skuJson", mode="before")
    @classmethod
    def normalize_sku_json(cls, value: Any) -> str | None:
        if value is None or isinstance(value, str):
            return value
        return json.dumps(value, separators=(",", ":"))


class DesiredEdge(BaseModel):
    sourceName: str = ""
    targetName: str = ""
    dependencyType: str = ""


class DesiredStateSpec(BaseModel):
    nodes: list[DesiredResourceNode] = Field(default_factory=list)
    edges: list[DesiredEdge] = Field(default_factory=list)
    scope: list[str] = Field(default_factory=list)


class SaveDesiredStateRequest(BaseModel):
    orgUrl: str = ""
    project: str = ""
    repository: str = ""
    pat: str = ""
    branch: str = "main"
    filePath: str = "infra/desired-state.json"
    commitMessage: str | None = None
    rawJson: str = "{}"
