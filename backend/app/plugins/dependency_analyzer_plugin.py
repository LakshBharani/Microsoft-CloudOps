from __future__ import annotations

import json
import re
import uuid
from contextvars import ContextVar
from typing import Annotated, Any, Awaitable, Callable

from semantic_kernel.functions import kernel_function

from app.azure_resources import get_infrastructure_nodes
from app.models import ResourceNode
from app.plugins.infra_analyzer_plugin import _resolve_subscription_id

ToolEventHandler = Callable[[str, str, str, bool | None], Awaitable[None]]
_tool_event_handler: ContextVar[ToolEventHandler | None] = ContextVar(
    "dependency_tool_event_handler", default=None)

STRONG_DEPENDENCY_TYPES = {
    "microsoft.web/sites->microsoft.web/serverfarms": "hosting",
    "microsoft.web/sites->microsoft.storage/storageaccounts": "data",
    "microsoft.compute/virtualmachines->microsoft.network/networkinterfaces": "network",
    "microsoft.network/networkinterfaces->microsoft.network/virtualnetworks": "network",
    "*->microsoft.resources/resourcegroups": "scope",
}

RISK_WEIGHTS = {
    "hosting": 0.9,
    "data": 0.8,
    "network": 0.95,
    "observability": 0.7,
}

DEFAULT_RISK_WEIGHT = 0.5
RESOURCE_ID_PATTERN = re.compile(r'/subscriptions/[^"\s]+', re.IGNORECASE)


def set_dependency_tool_event_handler(handler: ToolEventHandler | None) -> object:
    return _tool_event_handler.set(handler)


def reset_dependency_tool_event_handler(token: object) -> None:
    _tool_event_handler.reset(token)


async def _emit_tool_event(tool: str, invocation_id: str, phase: str, success: bool | None = None) -> None:
    handler = _tool_event_handler.get()
    if handler:
        await handler(tool, invocation_id, phase, success)


def _json(data: Any) -> str:
    return json.dumps(data, separators=(",", ":"), default=str)


def _normalize(value: str | None) -> str:
    return (value or "").strip().lower()


def _node_summary(node: ResourceNode) -> dict[str, Any]:
    return {
        "id": node.id,
        "name": node.name,
        "type": node.type,
        "resourceGroup": node.resourceGroup,
        "location": node.location,
    }


def _is_resource_group(node: ResourceNode) -> bool:
    return _normalize(node.type) == "microsoft.resources/resourcegroups"


def _find_resource_by_id(nodes: list[ResourceNode], resource_id: str) -> ResourceNode | None:
    normalized_id = _normalize(resource_id)
    return next((node for node in nodes if _normalize(node.id) == normalized_id), None)


def _try_get_parent_resource_id(resource_id: str) -> str | None:
    providers_segment = "/providers/"
    lowered = resource_id.lower()
    first = lowered.find(providers_segment)
    if first < 0:
        return None

    second = lowered.find(providers_segment, first + len(providers_segment))
    if second >= 0:
        return resource_id[:second]

    provider_path = resource_id[first + len(providers_segment):]
    segments = provider_path.split("/")
    if len(segments) <= 3 or len(segments) % 2 == 0:
        return None

    parent_provider_path = "/".join(segments[:-2])
    return f"{resource_id[:first]}{providers_segment}{parent_provider_path}"


def _extract_resource_ids(properties: dict[str, Any]) -> list[str]:
    raw = json.dumps(properties, separators=(",", ":"), default=str)
    return list(dict.fromkeys(match.group(0) for match in RESOURCE_ID_PATTERN.finditer(raw)))


def _dependency_type(source_type: str, target_type: str | None) -> str:
    source = _normalize(source_type)
    target = _normalize(target_type)

    exact = STRONG_DEPENDENCY_TYPES.get(f"{source}->{target}")
    if exact:
        return exact

    scope = STRONG_DEPENDENCY_TYPES.get(f"*->{target}")
    if scope:
        return scope

    haystack = f"{source} {target}"
    if "compute" in haystack:
        return "compute"
    if "network" in haystack:
        return "network"
    if "storage" in haystack:
        return "data"
    if "web" in haystack:
        return "service"
    return "generic"


def _risk_weight(dependency_type: str) -> float:
    return RISK_WEIGHTS.get(dependency_type, DEFAULT_RISK_WEIGHT)


def _resolve_node_dependency_ids(node: ResourceNode, all_nodes: list[ResourceNode]) -> list[str]:
    dependencies: list[str] = []
    parent_id = _try_get_parent_resource_id(node.id)
    if parent_id:
        dependencies.append(parent_id)

    node_type = _normalize(node.type)

    if "microsoft.web/sites" in node_type:
        server_farm_id = node.properties.get("serverFarmId")
        plan = (
            _find_resource_by_id(all_nodes, str(server_farm_id))
            if server_farm_id
            else next(
                (
                    candidate
                    for candidate in all_nodes
                    if "microsoft.web/serverfarms" in _normalize(candidate.type)
                    and _normalize(candidate.resourceGroup) == _normalize(node.resourceGroup)
                ),
                None,
            )
        )
        if plan:
            dependencies.append(plan.id)

    if "microsoft.compute/virtualmachines" in node_type:
        nic = next(
            (
                candidate
                for candidate in all_nodes
                if "microsoft.network/networkinterfaces" in _normalize(candidate.type)
                and _normalize(candidate.resourceGroup) == _normalize(node.resourceGroup)
            ),
            None,
        )
        if nic:
            dependencies.append(nic.id)

    if "microsoft.network/networkinterfaces" in node_type:
        vnet = next(
            (
                candidate
                for candidate in all_nodes
                if "microsoft.network/virtualnetworks" in _normalize(candidate.type)
                and _normalize(candidate.resourceGroup) == _normalize(node.resourceGroup)
            ),
            None,
        )
        if vnet:
            dependencies.append(vnet.id)

    resource_group = next(
        (
            candidate
            for candidate in all_nodes
            if _is_resource_group(candidate) and _normalize(candidate.name) == _normalize(node.resourceGroup)
        ),
        None,
    )
    if resource_group and not _is_resource_group(node):
        dependencies.append(resource_group.id)

    for resource_id in _extract_resource_ids(node.properties):
        if "/serverfarms/" not in resource_id.lower():
            dependencies.append(resource_id)

    return list(dict.fromkeys(dependencies))


def _resolve_dependency_edges(nodes: list[ResourceNode]) -> list[dict[str, Any]]:
    by_id = {_normalize(node.id): node for node in nodes}
    edges: list[dict[str, Any]] = []

    for node in nodes:
        for dependency_id in _resolve_node_dependency_ids(node, nodes):
            target = by_id.get(_normalize(dependency_id))
            if not target:
                continue

            dependency_type = _dependency_type(node.type, target.type)
            edges.append(
                {
                    "sourceId": node.id,
                    "targetId": target.id,
                    "dependencyType": dependency_type,
                    "riskWeight": _risk_weight(dependency_type),
                }
            )

    unique_edges: dict[str, dict[str, Any]] = {}
    for edge in edges:
        key = f"{edge['sourceId']}->{edge['targetId']}->{edge['dependencyType']}".lower()
        unique_edges[key] = edge

    return list(unique_edges.values())


class DependencyAnalyzerPlugin:
    """Read-only dependency analysis tools for dependency-analyzer."""

    @kernel_function(name="analyze_resource_dependencies", description="Analyze dependency edges for one Azure resource by full ARM resource id. Read-only.")
    async def analyze_resource_dependencies(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_id: Annotated[str, "Full ARM resource id"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("analyze_resource_dependencies", invocation_id, "start")
        try:
            nodes = await get_infrastructure_nodes(_resolve_subscription_id(subscription_id))
            resource = _find_resource_by_id(nodes, resource_id)
            if not resource:
                await _emit_tool_event("analyze_resource_dependencies", invocation_id, "end", True)
                return _json({"resourceId": resource_id, "found": False, "resource": None, "dependencies": []})

            edges = _resolve_dependency_edges(nodes)
            related_edges = [
                edge
                for edge in edges
                if _normalize(edge["sourceId"]) == _normalize(resource_id)
                or _normalize(edge["targetId"]) == _normalize(resource_id)
            ]
            by_id = {_normalize(node.id): node for node in nodes}
            related_ids = {_normalize(resource_id)}
            for edge in related_edges:
                related_ids.add(_normalize(edge["sourceId"]))
                related_ids.add(_normalize(edge["targetId"]))

            await _emit_tool_event("analyze_resource_dependencies", invocation_id, "end", True)
            return _json(
                {
                    "resourceId": resource.id,
                    "found": True,
                    "resource": _node_summary(resource),
                    "relatedResources": [
                        _node_summary(by_id[node_id])
                        for node_id in related_ids
                        if node_id in by_id
                    ],
                    "dependencies": related_edges,
                }
            )
        except Exception:
            await _emit_tool_event("analyze_resource_dependencies", invocation_id, "end", False)
            raise

    @kernel_function(name="analyze_resource_group_dependencies", description="Analyze dependency edges for resources in one Azure resource group. Read-only.")
    async def analyze_resource_group_dependencies(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Exact resource group name"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("analyze_resource_group_dependencies", invocation_id, "start")
        try:
            nodes = await get_infrastructure_nodes(_resolve_subscription_id(subscription_id))
            group_ids = {
                _normalize(node.id)
                for node in nodes
                if _normalize(node.resourceGroup) == _normalize(resource_group_name)
                or (_is_resource_group(node) and _normalize(node.name) == _normalize(resource_group_name))
            }
            edges = [
                edge
                for edge in _resolve_dependency_edges(nodes)
                if _normalize(edge["sourceId"]) in group_ids or _normalize(edge["targetId"]) in group_ids
            ]
            by_id = {_normalize(node.id): node for node in nodes}
            related_ids = set(group_ids)
            for edge in edges:
                related_ids.add(_normalize(edge["sourceId"]))
                related_ids.add(_normalize(edge["targetId"]))

            await _emit_tool_event("analyze_resource_group_dependencies", invocation_id, "end", True)
            return _json(
                {
                    "resourceGroup": resource_group_name,
                    "found": bool(group_ids),
                    "relatedResources": [
                        _node_summary(by_id[node_id])
                        for node_id in related_ids
                        if node_id in by_id
                    ],
                    "dependencies": edges,
                }
            )
        except Exception:
            await _emit_tool_event("analyze_resource_group_dependencies", invocation_id, "end", False)
            raise
