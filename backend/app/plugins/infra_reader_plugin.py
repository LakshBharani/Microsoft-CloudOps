from __future__ import annotations

import json
import os
import uuid
from contextvars import ContextVar
from typing import Annotated, Any, Awaitable, Callable

from semantic_kernel.functions import kernel_function

from app.azure_resources import get_infrastructure_nodes
from app.models import ResourceNode

ToolEventHandler = Callable[[str, str, str, bool | None], Awaitable[None]]
_tool_event_handler: ContextVar[ToolEventHandler | None] = ContextVar(
    "infra-reader_tool_event_handler", default=None)


def _json(data: Any) -> str:
    return json.dumps(data, separators=(",", ":"), default=str)


def _normalize(value: str | None) -> str:
    return (value or "").strip().lower()


def _matches(value: str, expected: str | None) -> bool:
    return expected is None or _normalize(value) == _normalize(expected)


def _subscription_id_from_env() -> str:
    subscription_id = os.environ.get("AZURE_SUBSCRIPTION_ID", "").strip()
    if subscription_id:
        return subscription_id

    connection_string = os.environ.get("AZURE_AI_AGENT_PROJECT_CONNECTION_STRING", "")
    parts = [part.strip() for part in connection_string.split(";")]
    return parts[1] if len(parts) >= 2 else ""


def _resolve_subscription_id(subscription_id: str) -> str:
    candidate = subscription_id.strip()
    if _normalize(candidate) in {"", "default", "current", "my subscription"}:
        candidate = _subscription_id_from_env()

    try:
        return str(uuid.UUID(candidate))
    except ValueError as exc:
        raise ValueError(
            "subscription_id must be a valid Azure subscription GUID."
        ) from exc


def _is_resource_group(node: ResourceNode) -> bool:
    return _normalize(node.type) == "microsoft.resources/resourcegroups"


def _node_summary(node: ResourceNode) -> dict[str, Any]:
    return {
        "id": node.id,
        "name": node.name,
        "type": node.type,
        "resourceGroup": node.resourceGroup,
        "location": node.location,
        "kind": node.kind,
        "tags": node.tags,
    }


def _find_resource_by_id(nodes: list[ResourceNode], resource_id: str) -> ResourceNode | None:
    normalized_id = _normalize(resource_id)
    return next((node for node in nodes if _normalize(node.id) == normalized_id), None)


def set_infra_reader_tool_event_handler(handler: ToolEventHandler | None) -> object:
    return _tool_event_handler.set(handler)


def reset_infra_reader_tool_event_handler(token: object) -> None:
    _tool_event_handler.reset(token)


async def _emit_tool_event(tool: str, invocation_id: str, phase: str, success: bool | None = None) -> None:
    handler = _tool_event_handler.get()
    if handler:
        await handler(tool, invocation_id, phase, success)


class InfraReaderPlugin:
    """Read-only Azure infrastructure tools for infra-reader-agent."""

    @kernel_function(name="list_resource_groups", description="List Azure resource groups in a subscription. Read-only.")
    async def list_resource_groups(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("list_resource_groups", invocation_id, "start")
        try:
            nodes = await get_infrastructure_nodes(_resolve_subscription_id(subscription_id))
            resource_groups = sorted(
                (_node_summary(node) for node in nodes if _is_resource_group(node)),
                key=lambda node: node["name"].lower(),
            )
            await _emit_tool_event("list_resource_groups", invocation_id, "end", True)
            return _json({"resourceGroups": resource_groups})
        except Exception:
            await _emit_tool_event("list_resource_groups", invocation_id, "end", False)
            raise

    @kernel_function(name="find_resource_group", description="Find one Azure resource group by exact name. Read-only.")
    async def find_resource_group(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        name: Annotated[str, "Exact resource group name"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("find_resource_group", invocation_id, "start")
        try:
            nodes = await get_infrastructure_nodes(_resolve_subscription_id(subscription_id))
            matches = [
                node.model_dump()
                for node in nodes
                if _is_resource_group(node) and _matches(node.name, name)
            ]
            await _emit_tool_event("find_resource_group", invocation_id, "end", True)
            return _json({"name": name, "count": len(matches), "resourceGroups": matches})
        except Exception:
            await _emit_tool_event("find_resource_group", invocation_id, "end", False)
            raise

    @kernel_function(name="list_resources", description="List Azure resources, optionally scoped to one resource group. Read-only.")
    async def list_resources(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str | None, "Optional resource group name"] = None,
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("list_resources", invocation_id, "start")
        try:
            nodes = await get_infrastructure_nodes(_resolve_subscription_id(subscription_id))
            resources = [
                _node_summary(node)
                for node in nodes
                if not _is_resource_group(node) and _matches(node.resourceGroup, resource_group_name)
            ]
            resources.sort(key=lambda node: (node["resourceGroup"].lower(), node["type"].lower(), node["name"].lower()))
            await _emit_tool_event("list_resources", invocation_id, "end", True)
            return _json({"resourceGroup": resource_group_name, "count": len(resources), "resources": resources})
        except Exception:
            await _emit_tool_event("list_resources", invocation_id, "end", False)
            raise

    @kernel_function(name="find_resource", description="Find Azure resources by exact name, type, and/or resource group. Read-only.")
    async def find_resource(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        name: Annotated[str | None, "Optional exact resource name"] = None,
        resource_type: Annotated[str | None, "Optional exact Azure resource type"] = None,
        resource_group_name: Annotated[str | None, "Optional resource group name"] = None,
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("find_resource", invocation_id, "start")
        try:
            nodes = await get_infrastructure_nodes(_resolve_subscription_id(subscription_id))
            matches = [
                _node_summary(node)
                for node in nodes
                if not _is_resource_group(node)
                and _matches(node.name, name)
                and _matches(node.type, resource_type)
                and _matches(node.resourceGroup, resource_group_name)
            ]
            matches.sort(key=lambda node: (node["resourceGroup"].lower(), node["type"].lower(), node["name"].lower()))
            await _emit_tool_event("find_resource", invocation_id, "end", True)
            return _json({"count": len(matches), "resources": matches})
        except Exception:
            await _emit_tool_event("find_resource", invocation_id, "end", False)
            raise

    @kernel_function(name="get_resource_properties", description="Get properties for one Azure resource by full ARM resource id. Read-only.")
    async def get_resource_properties(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_id: Annotated[str, "Full ARM resource id"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("get_resource_properties", invocation_id, "start")
        try:
            nodes = await get_infrastructure_nodes(_resolve_subscription_id(subscription_id))
            resource = _find_resource_by_id(nodes, resource_id)
            await _emit_tool_event("get_resource_properties", invocation_id, "end", True)
            return _json(
                {
                    "resourceId": resource_id,
                    "found": resource is not None,
                    "properties": resource.properties if resource else None,
                }
            )
        except Exception:
            await _emit_tool_event("get_resource_properties", invocation_id, "end", False)
            raise

    @kernel_function(name="get_resource_group_properties", description="Get properties for one Azure resource group by exact name. Read-only.")
    async def get_resource_group_properties(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Exact resource group name"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("get_resource_group_properties", invocation_id, "start")
        try:
            nodes = await get_infrastructure_nodes(_resolve_subscription_id(subscription_id))
            resource_group = next(
                (
                    node
                    for node in nodes
                    if _is_resource_group(node) and _matches(node.name, resource_group_name)
                ),
                None,
            )
            await _emit_tool_event("get_resource_group_properties", invocation_id, "end", True)
            return _json(
                {
                    "resourceGroup": resource_group_name,
                    "found": resource_group is not None,
                    "properties": resource_group.properties if resource_group else None,
                }
            )
        except Exception:
            await _emit_tool_event("get_resource_group_properties", invocation_id, "end", False)
            raise
