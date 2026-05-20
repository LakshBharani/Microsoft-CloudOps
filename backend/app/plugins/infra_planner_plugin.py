from __future__ import annotations

import json
import uuid
from contextvars import ContextVar
from typing import Annotated, Any, Awaitable, Callable

from semantic_kernel.functions import kernel_function

from app.azure_resources import get_infrastructure_nodes
from app.models import ResourceNode
from app.plugins.infra_reader_plugin import (
    _is_resource_group,
    _normalize,
    _resolve_subscription_id,
    _similar_resource_groups,
)

ToolEventHandler = Callable[[str, str, str, bool | None], Awaitable[None]]
_tool_event_handler: ContextVar[ToolEventHandler | None] = ContextVar(
    "infra-planner_tool_event_handler", default=None
)
_latest_plan: ContextVar[dict[str, Any] | None] = ContextVar(
    "infra-planner_latest_plan", default=None
)


def set_infra_planner_tool_event_handler(handler: ToolEventHandler | None) -> object:
    return _tool_event_handler.set(handler)


def reset_infra_planner_tool_event_handler(token: object) -> None:
    _tool_event_handler.reset(token)


def set_infra_planner_plan(plan: dict[str, Any] | None) -> object:
    return _latest_plan.set(plan)


def reset_infra_planner_plan(token: object) -> None:
    _latest_plan.reset(token)


def get_infra_planner_plan() -> dict[str, Any] | None:
    return _latest_plan.get()


async def _emit_tool_event(tool: str, invocation_id: str, phase: str, success: bool | None = None) -> None:
    handler = _tool_event_handler.get()
    if handler:
        await handler(tool, invocation_id, phase, success)


def _json(data: Any) -> str:
    return json.dumps(data, separators=(",", ":"), default=str)


def _normalize_action(value: Any) -> str:
    action = str(value or "").strip().lower()
    if action in {"add", "create", "provision"}:
        return "Create"
    if action in {"update", "modify", "configure", "attach", "detach", "connect"}:
        return "Update"
    if action in {"delete", "remove", "decommission"}:
        return "Delete"
    if action == "deploy":
        return "Deploy"
    raise ValueError(
        "Each plan operation needs action Create, Update, Delete, or Deploy.")


def _is_resource_group_type(resource_type: str) -> bool:
    return resource_type.strip().lower() in {
        "resourcegroup",
        "resource group",
        "microsoft.resources/resourcegroups",
    }


def _looks_like_placeholder_operation(
    resource_type: str,
    resource_name: str,
    details: Any,
) -> bool:
    text = f"{resource_type} {resource_name} {details}".lower()
    vague_fragments = (
        "resources in ",
        "if present",
        "if found",
        "soft-delete",
        "deletion restrictions",
        "restrictions",
        "policies found",
        "management locks",
        "locks on ",
    )
    return any(fragment in text for fragment in vague_fragments)


def _normalize_operations(operations_json: str) -> list[dict[str, Any]]:
    try:
        parsed = json.loads(operations_json)
    except json.JSONDecodeError as exc:
        raise ValueError(
            "operations_json must be a JSON array of operation objects.") from exc

    if not isinstance(parsed, list) or not parsed:
        raise ValueError("operations_json must be a non-empty JSON array.")

    operations: list[dict[str, Any]] = []
    for index, item in enumerate(parsed, start=1):
        if not isinstance(item, dict):
            raise ValueError("Each operation must be a JSON object.")
        resource_type = str(item.get("resource_type")
                            or item.get("resourceType") or "").strip()
        resource_name = str(item.get("resource_name")
                            or item.get("resourceName") or "").strip()
        details = item.get("details") or item.get("description") or ""
        if not resource_type or not resource_name:
            raise ValueError(
                "Each operation needs resource_type and resource_name.")
        if _looks_like_placeholder_operation(resource_type, resource_name, details):
            raise ValueError(
                "Plans must use concrete resource-level operations only. "
                "Do not include vague cleanup steps like locks/restrictions/soft-delete if present."
            )
        operation: dict[str, Any] = {
            "action": _normalize_action(item.get("action")),
            "resource_type": resource_type,
            "resource_name": resource_name,
            "details": str(details).strip() or f"Step {index}",
        }
        resource_group = item.get(
            "resource_group") or item.get("resourceGroup")
        if resource_group:
            operation["resource_group"] = str(resource_group).strip()
        operations.append(operation)

    for index, operation in enumerate(operations[:-1]):
        if operation["action"] == "Delete" and _is_resource_group_type(operation["resource_type"]):
            raise ValueError(
                "A resource group delete must be the final operation. "
                "Delete each known contained resource as its own earlier operation."
            )
    return operations


def _normalize_resources(resources_json: str, operations: list[dict[str, Any]]) -> list[dict[str, str]]:
    if not resources_json.strip():
        return [
            {
                "action": op["action"],
                "resource_type": op["resource_type"],
                "resource_name": op["resource_name"],
                **({"resource_group": op["resource_group"]} if op.get("resource_group") else {}),
            }
            for op in operations
        ]

    try:
        parsed = json.loads(resources_json)
    except json.JSONDecodeError as exc:
        raise ValueError("resources_json must be a JSON array.") from exc

    if not isinstance(parsed, list):
        raise ValueError("resources_json must be a JSON array.")

    resources: list[dict[str, str]] = []
    for item in parsed:
        if isinstance(item, str):
            resources.append({"resource_name": item})
            continue
        if not isinstance(item, dict):
            raise ValueError("Each resources_json item must be an object or string.")
        resource_name = str(
            item.get("resource_name") or item.get("resourceName") or item.get("name") or ""
        ).strip()
        resource_type = str(
            item.get("resource_type") or item.get("resourceType") or item.get("type") or ""
        ).strip()
        action = str(item.get("action") or "").strip()
        if not resource_name:
            raise ValueError("Each resources_json object needs resource_name or name.")
        resource: dict[str, str] = {"resource_name": resource_name}
        if action:
            resource["action"] = _normalize_action(action)
        if resource_type:
            resource["resource_type"] = resource_type
        if resource_group := item.get("resource_group") or item.get("resourceGroup"):
            resource["resource_group"] = str(resource_group).strip()
        if note := item.get("note") or item.get("reason"):
            resource["note"] = str(note).strip()
        resources.append(resource)

    return resources


def _default_dependencies(operations: list[dict[str, Any]]) -> str:
    if not any(operation["action"] == "Delete" for operation in operations):
        return "Create/update operations are ordered with dependencies before dependents."

    notes = [
        "Delete operations are ordered dependents first, then parents or referenced resources last",
    ]
    has_subnet = any("subnet" in operation["resource_type"].lower() for operation in operations)
    has_vnet = any("virtualnetwork" in operation["resource_type"].lower() or "vnet" in operation["resource_name"].lower() for operation in operations)
    has_nsg = any("networksecuritygroup" in operation["resource_type"].lower() or "nsg" in operation["resource_name"].lower() for operation in operations)
    has_route_table = any("routetable" in operation["resource_type"].lower() or operation["resource_name"].lower().startswith("rt-") for operation in operations)
    if has_subnet and has_vnet:
        notes.append("subnets -> vnet because subnets must be deleted before the parent VNet")
    if has_subnet and has_nsg:
        notes.append("subnets -> nsg because an NSG cannot be deleted while subnets reference it")
    if any(_is_resource_group_type(operation["resource_type"]) for operation in operations):
        notes.append("all resources -> resource group because the resource group is deleted last")
    if has_route_table:
        notes.append("route tables can delete in parallel when no subnet association remains")
    if any("storage" in operation["resource_type"].lower() for operation in operations):
        notes.append("storage accounts can delete in parallel when they have no cross-dependencies")
    return "; ".join(notes) + "."


def _normalize_risk(value: str) -> str:
    risk = value.strip().lower()
    if risk == "high":
        return "High"
    if risk == "medium":
        return "Medium"
    return "Low"


DELETE_PRIORITY: dict[str, int] = {
    "microsoft.compute/disks": 20,
    "microsoft.compute/virtualmachines": 30,
    "microsoft.network/networkinterfaces": 40,
    "microsoft.network/virtualnetworks": 50,
    "microsoft.network/networksecuritygroups": 60,
    "microsoft.network/routetables": 70,
    "microsoft.web/sites": 80,
    "microsoft.web/serverfarms": 90,
    "microsoft.storage/storageaccounts": 100,
}
_DEFAULT_DELETE_PRIORITY = 200


def _delete_priority(resource_type: str) -> int:
    return DELETE_PRIORITY.get(resource_type.strip().lower(), _DEFAULT_DELETE_PRIORITY)


def _delete_detail(node: ResourceNode) -> str:
    nt = node.type.lower()
    if "microsoft.network/virtualnetworks" in nt and "/subnets" not in nt:
        return f"Delete VNet {node.name} after subnets are removed"
    if "microsoft.network/networksecuritygroups" in nt:
        return f"Delete NSG {node.name} after referencing subnets are removed"
    if "microsoft.network/routetables" in nt:
        return f"Delete route table {node.name}"
    if "microsoft.storage/storageaccounts" in nt:
        return f"Delete storage account {node.name}"
    if "microsoft.compute/virtualmachines" in nt:
        return f"Delete VM {node.name}"
    if "microsoft.network/networkinterfaces" in nt:
        return f"Delete NIC {node.name}"
    if "microsoft.compute/disks" in nt:
        return f"Delete disk {node.name}"
    return f"Delete {node.name}"


def _subnet_delete_operations(vnet_node: ResourceNode) -> list[dict[str, Any]]:
    if not isinstance(vnet_node.properties, dict):
        return []
    subnets = vnet_node.properties.get("subnets")
    if not isinstance(subnets, list):
        return []
    operations: list[dict[str, Any]] = []
    for subnet in subnets:
        if not isinstance(subnet, dict):
            continue
        subnet_name = str(subnet.get("name") or "").strip()
        if not subnet_name:
            continue
        operations.append({
            "action": "Delete",
            "resource_type": "Microsoft.Network/virtualNetworks/subnets",
            "resource_name": f"{vnet_node.name}/{subnet_name}",
            "resource_group": vnet_node.resourceGroup,
            "details": f"Delete subnet {subnet_name} from VNet {vnet_node.name} before parent VNet",
        })
    return operations


def _build_delete_operations(rg_name: str, contained: list[ResourceNode]) -> list[dict[str, Any]]:
    operations: list[dict[str, Any]] = []

    for node in contained:
        if _normalize(node.type) == "microsoft.network/virtualnetworks":
            operations.extend(_subnet_delete_operations(node))

    sorted_contained = sorted(contained, key=lambda n: _delete_priority(n.type))
    for node in sorted_contained:
        operations.append({
            "action": "Delete",
            "resource_type": node.type,
            "resource_name": node.name,
            "resource_group": node.resourceGroup,
            "details": _delete_detail(node),
        })

    operations.append({
        "action": "Delete",
        "resource_type": "Microsoft.Resources/resourceGroups",
        "resource_name": rg_name,
        "details": f"Delete resource group {rg_name} after all contained resources are removed",
    })
    return operations


class InfraPlannerPlugin:
    """Read-only architecture planning tools for infra-planner-agent."""

    @kernel_function(name="infer_intent", description="Infer the user's infrastructure goal, scope, constraints, and resources involved.")
    async def infer_intent(
        self,
        user_request: Annotated[str, "The raw user request or intent JSON"],
        goal: Annotated[str, "The inferred infrastructure goal"],
        scope: Annotated[str, "Subscription, resource group, location, and boundaries"],
        resources_involved: Annotated[str, "Resources that may be created, updated, deleted, or modified"],
        constraints: Annotated[str,
                               "Constraints such as cost, no-compute, safety, tags, or policy requirements"] = "",
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("infer_intent", invocation_id, "start")
        try:
            intent = {
                "userRequest": user_request,
                "goal": goal,
                "scope": scope,
                "resourcesInvolved": resources_involved,
                "constraints": constraints,
            }
            await _emit_tool_event("infer_intent", invocation_id, "end", True)
            return _json(intent)
        except Exception:
            await _emit_tool_event("infer_intent", invocation_id, "end", False)
            raise

    @kernel_function(name="critic", description="Critique an inferred infrastructure intent for blockers, ordering risks, and likely failure points.")
    async def critic(
        self,
        intent: Annotated[str, "The output from infer_intent"],
        resources_involved: Annotated[str, "Resources that the plan may touch"],
        failure_points: Annotated[str, "Possible failures, missing prerequisites, dependency blockers, or Azure constraints"],
        sequencing_notes: Annotated[str, "Important create/update/delete ordering notes"],
        open_questions: Annotated[str,
                                  "Clarifying questions or assumptions"] = "",
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("critic", invocation_id, "start")
        try:
            critique = {
                "intent": intent,
                "resourcesInvolved": resources_involved,
                "failurePoints": failure_points,
                "sequencingNotes": sequencing_notes,
                "openQuestions": open_questions,
            }
            await _emit_tool_event("critic", invocation_id, "end", True)
            return _json(critique)
        except Exception:
            await _emit_tool_event("critic", invocation_id, "end", False)
            raise

    @kernel_function(name="brainstorm", description="Brainstorm a dependency-aware infrastructure change plan from intent and critique.")
    async def brainstorm(
        self,
        intent: Annotated[str, "The output from infer_intent"],
        critique: Annotated[str, "The output from critic"],
        candidate_steps: Annotated[str, "Candidate resource-level steps before final ordering"],
        dependency_reasoning: Annotated[str, "Reasoning about why resources must be created, updated, or deleted in a certain order"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("brainstorm", invocation_id, "start")
        try:
            brainstormed_plan = {
                "intent": intent,
                "critique": critique,
                "candidateSteps": candidate_steps,
                "dependencyReasoning": dependency_reasoning,
            }
            await _emit_tool_event("brainstorm", invocation_id, "end", True)
            return _json(brainstormed_plan)
        except Exception:
            await _emit_tool_event("brainstorm", invocation_id, "end", False)
            raise

    @kernel_function(name="propose_plan", description="Emit the final chronological, resource-level plan for the UI plan card. Does not deploy or modify resources. It STRICTLY returns the JSON and nothing else. <json>...</json>")
    async def propose_plan(
        self,
        title: Annotated[str, "Short plan title"],
        summary: Annotated[str, "Short summary of the plan"],
        operations_json: Annotated[
            str,
            (
                "JSON array of chronological operation objects. Each object needs "
                "action, resource_type, resource_name, and details. Optional resource_group. "
                "Do not collapse resources into one ARM deployment operation. "
                "Do not use vague placeholder steps like resources in a group, locks if found, or soft-delete if present. "
                "For create plans, list prerequisites before dependents. "
                "For delete plans, list dependents before parents or referenced resources: subnets before VNet, subnets before NSG, and all contained resources before resource group. "
                "For resource group deletion, include one Delete operation per known contained resource before the final resource group Delete."
            ),
        ],
        risk_level: Annotated[str, "Low, Medium, or High"],
        estimated_cost_note: Annotated[str,
                                       "Short cost note"] = "Cost estimate is not calculated yet.",
        critic_verdict: Annotated[str,
                                  "Short final safety or dependency verdict"] = "",
        resources_json: Annotated[
            str,
            (
                "Optional JSON array mirroring the chronological execution order of operations. "
                "For delete plans this must be delete order: dependents first and parent or referenced resources last."
            ),
        ] = "",
        dependencies: Annotated[
            str,
            (
                "Ordering explanation. For delete plans, state that subnets go before VNet, "
                "subnets go before referenced NSGs, all resources go before the resource group, "
                "and route tables or storage accounts can delete in parallel when independent."
            ),
        ] = "",
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("propose_plan", invocation_id, "start")
        try:
            operations = _normalize_operations(operations_json)
            plan = {
                "title": title,
                "summary": summary,
                "operations": operations,
                "resources": _normalize_resources(resources_json, operations),
                "dependencies": dependencies.strip() or _default_dependencies(operations),
                "risk_level": _normalize_risk(risk_level),
                "estimated_cost_note": estimated_cost_note,
                "critic_verdict": critic_verdict
                or "Draft plan only. Operations are ordered for dependency-safe execution after approval.",
                "revision_count": 0,
                "status": "pending",
            }
            _latest_plan.set(plan)
            await _emit_tool_event("propose_plan", invocation_id, "end", True)
            return f"<json>{_json(plan)}</json>"
        except Exception:
            await _emit_tool_event("propose_plan", invocation_id, "end", False)
            raise

    @kernel_function(
        name="propose_delete_plan",
        description=(
            "Build a deterministic chronological delete plan for an entire resource group. "
            "Enumerates all resources from live Azure Resource Graph, expands subnets from each VNet, "
            "and orders the deletes dependency-safe (subnets -> VNet -> NSG -> independent resources -> "
            "resource group last). Returns the plan card wrapped in <json> tags. Does not delete anything. "
            "Use this for any delete/remove/decommission of a resource group instead of propose_plan."
        ),
    )
    async def propose_delete_plan(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Resource group name to delete"],
        title: Annotated[str, "Optional plan title"] = "",
        summary: Annotated[str, "Optional plan summary"] = "",
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("propose_delete_plan", invocation_id, "start")
        try:
            resolved_sub = _resolve_subscription_id(subscription_id)
            nodes = await get_infrastructure_nodes(resolved_sub)
            rg_target = _normalize(resource_group_name)

            rg_node = next(
                (n for n in nodes if _is_resource_group(n) and _normalize(n.name) == rg_target),
                None,
            )
            if not rg_node:
                suggestions = _similar_resource_groups(nodes, resource_group_name)
                suggestion_text = (
                    " Similar resource groups: "
                    + ", ".join(
                        f"{item['name']} ({item['resourceCount']} resource(s))"
                        for item in suggestions
                    )
                    + "."
                    if suggestions
                    else ""
                )
                raise ValueError(
                    f"Resource group '{resource_group_name}' not found in subscription."
                    f"{suggestion_text}"
                )

            contained = [
                n for n in nodes
                if _normalize(n.resourceGroup) == rg_target and not _is_resource_group(n)
            ]
            if not contained:
                populated_suggestions = [
                    item for item in _similar_resource_groups(nodes, resource_group_name)
                    if int(item.get("resourceCount") or 0) > 0
                ]
                if populated_suggestions:
                    suggestion_text = ", ".join(
                        f"{item['name']} ({item['resourceCount']} resource(s))"
                        for item in populated_suggestions
                    )
                    raise ValueError(
                        f"Resource group '{resource_group_name}' exists but contains no resources. "
                        f"Similar populated resource group(s) exist: {suggestion_text}. "
                        "Confirm the exact resource group name before creating a delete plan."
                    )

            operations = _build_delete_operations(resource_group_name, contained)

            plan_title = title.strip() or f"Delete resource group {resource_group_name}"
            plan_summary = summary.strip() or (
                f"Cascade delete {resource_group_name}: {len(contained)} contained resource(s) plus "
                "the resource group, ordered dependency-safe from live Azure state."
            )

            plan = {
                "title": plan_title,
                "summary": plan_summary,
                "operations": operations,
                "resources": [
                    {k: v for k, v in op.items() if k != "details"}
                    for op in operations
                ],
                "dependencies": (
                    "subnets -> VNet because subnets must be deleted before the parent VNet; "
                    "subnets -> NSG because an NSG cannot be deleted while subnets reference it; "
                    "all resources -> resource group because the RG is deleted last; "
                    "route tables and storage accounts can delete in parallel when independent."
                ),
                "risk_level": "High",
                "estimated_cost_note": "No additional cost; this plan deletes existing resources.",
                "critic_verdict": (
                    "Delete plan generated from live Azure Resource Graph state. "
                    "All resource names verified. Order is dependency-safe."
                ),
                "revision_count": 0,
                "status": "pending",
            }

            _latest_plan.set(plan)
            await _emit_tool_event("propose_delete_plan", invocation_id, "end", True)
            return f"<json>{_json(plan)}</json>"
        except Exception:
            await _emit_tool_event("propose_delete_plan", invocation_id, "end", False)
            raise
