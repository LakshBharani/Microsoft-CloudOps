from __future__ import annotations

import json
from typing import Any

import aiohttp
from azure.core.exceptions import AzureError
from azure.identity.aio import DefaultAzureCredential
from fastapi import HTTPException

from app.models import ResourceNode


RESOURCE_GRAPH_URL = "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01"
RESOURCE_GRAPH_QUERY = (
    "Resources | project id, name, type, resourceGroup, location, tags, properties, sku, kind"
    " | union (ResourceContainers | where type == 'microsoft.resources/subscriptions/resourcegroups'"
    " | project id, name, type = 'Microsoft.Resources/resourceGroups', resourceGroup = name, location, tags, properties)"
)


def _string_tags(value: Any) -> dict[str, str]:
    if not isinstance(value, dict):
        return {}
    return {str(k): v for k, v in value.items() if isinstance(v, str)}


def _resource_from_row(row: dict[str, Any]) -> ResourceNode:
    sku = row.get("sku")
    return ResourceNode(
        id=str(row.get("id") or ""),
        name=str(row.get("name") or ""),
        type=str(row.get("type") or ""),
        resourceGroup=str(row.get("resourceGroup") or ""),
        location=row.get("location") if isinstance(row.get("location"), str) else "",
        tags=_string_tags(row.get("tags")),
        properties=row.get("properties") if isinstance(row.get("properties"), dict) else {},
        skuJson=json.dumps(sku, separators=(",", ":")) if isinstance(sku, dict) else None,
        kind=row.get("kind") if isinstance(row.get("kind"), str) else None,
    )


async def get_infrastructure_nodes(subscription_id: str) -> list[ResourceNode]:
    credential = DefaultAzureCredential()
    try:
        token = await credential.get_token("https://management.azure.com/.default")
    except AzureError as exc:
        await credential.close()
        raise HTTPException(status_code=400, detail=f"Azure authentication failed: {exc}") from exc

    try:
        async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=45)) as session:
            async with session.post(
                RESOURCE_GRAPH_URL,
                headers={"Authorization": f"Bearer {token.token}", "Content-Type": "application/json"},
                json={"subscriptions": [subscription_id], "query": RESOURCE_GRAPH_QUERY},
            ) as response:
                body = await response.text()
                if response.status < 200 or response.status >= 300:
                    raise HTTPException(
                        status_code=400,
                        detail=f"Azure Resource Graph query failed ({response.status} {response.reason}): {body}",
                    )
    finally:
        await credential.close()

    try:
        payload = json.loads(body)
        rows = payload.get("data", [])
    except json.JSONDecodeError as exc:
        raise HTTPException(status_code=400, detail=f"Azure Resource Graph returned invalid JSON: {exc}") from exc

    if not isinstance(rows, list):
        raise HTTPException(status_code=400, detail="Azure Resource Graph response did not include a data array.")
    return [_resource_from_row(row) for row in rows if isinstance(row, dict)]
