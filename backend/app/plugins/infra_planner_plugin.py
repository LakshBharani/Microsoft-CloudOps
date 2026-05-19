from __future__ import annotations

import json
import uuid
from contextvars import ContextVar
from typing import Annotated, Any, Awaitable, Callable

from semantic_kernel.functions import kernel_function

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
    return operations


def _normalize_risk(value: str) -> str:
    risk = value.strip().lower()
    if risk == "high":
        return "High"
    if risk == "medium":
        return "Medium"
    return "Low"


class InfraPlannerPlugin:
    """Read-only architecture planning tools for infra-planner-agent."""

    @kernel_function(name="infer_intent", description="Infer the user's infrastructure goal, scope, constraints, and resources involved.")
    async def infer_intent(
        self,
        user_request: Annotated[str, "The raw user request or intent JSON"],
        goal: Annotated[str, "The inferred infrastructure goal"],
        scope: Annotated[str, "Subscription, resource group, location, and boundaries"],
        resources_involved: Annotated[str, "Resources that may be created, updated, deleted, or touched"],
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
                "Do not collapse resources into one ARM deployment operation."
            ),
        ],
        risk_level: Annotated[str, "Low, Medium, or High"],
        estimated_cost_note: Annotated[str,
                                       "Short cost note"] = "Cost estimate is not calculated yet.",
        critic_verdict: Annotated[str,
                                  "Short final safety or dependency verdict"] = "",
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("propose_plan", invocation_id, "start")
        try:
            plan = {
                "title": title,
                "summary": summary,
                "operations": _normalize_operations(operations_json),
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
