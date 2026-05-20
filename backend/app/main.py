import asyncio
import json
import uuid
from typing import Any

from fastapi import FastAPI, HTTPException, Query, Response
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, ValidationError

from app.constants import Constants
from app.group_chats.cloudops_group_chat import (
    ask_cloudops_group_chat,
    reset_cloudops_group_chat,
)
from app.config import get_settings
from app.azure_resources import get_infrastructure_nodes
from app.devops import AzureDevOpsConfig, get_desired_state, push_desired_state
from app.diff import compute_diff
from app.models import DesiredStateSpec, SaveDesiredStateRequest

app = FastAPI(title="CloudOps Backend")
app.add_middleware(
    CORSMiddleware,
    allow_origins=[
        "http://localhost:3000",
        "http://127.0.0.1:3000",
        "http://172.31.23.219:3000",
    ],
    allow_origin_regex=r"http://(localhost|127\.0\.0\.1|172\.\d+\.\d+\.\d+):\d+",
    allow_methods=["*"],
    allow_headers=["*"],
)


class AgentChatRequest(BaseModel):
    message: str
    subscriptionId: str = ""
    sessionId: str | None = None


class QuestionAnswerRequest(BaseModel):
    answer: str


@app.get("/health")
async def health() -> dict[str, str]:
    return {"status": "ok"}


@app.on_event("shutdown")
async def shutdown() -> None:
    await reset_cloudops_group_chat()


@app.get("/config")
async def config() -> dict[str, object]:
    s = get_settings()
    return {
        "model": s.azure_ai_agent_model,
        "project_configured": bool(s.azure_ai_project_connection_string),
        "serpapi_configured": bool(s.serpapi_key),
    }


def _looks_like_intent(payload: Any) -> bool:
    return isinstance(payload, dict) and ("components" in payload or "intent" in payload)


def _resolve_subscription_id(subscription_id: str) -> str:
    resolved = subscription_id.strip() or get_settings().azure_subscription_id.strip()
    if not resolved:
        raise HTTPException(
            status_code=400,
            detail="Missing subscriptionId. Provide ?subscriptionId=... or set AZURE_SUBSCRIPTION_ID.",
        )
    return resolved


@app.get("/api/infra")
async def infra(subscriptionId: str = Query(default="")) -> dict[str, list[dict[str, object]]]:
    subscription_id = _resolve_subscription_id(subscriptionId)
    nodes = await get_infrastructure_nodes(subscription_id)
    return {
        "nodes": [node.model_dump(by_alias=True) for node in nodes],
        "edges": [],
    }


def _sse_event(event: dict[str, Any]) -> str:
    return f"data: {json.dumps(event, separators=(',', ':'))}\n\n"


def _plan_event(plan: dict[str, Any] | None, session_id: str) -> dict[str, Any] | None:
    if not plan:
        return None

    return {
        "type": "plan",
        "data": {
            "plan_id": f"plan-{uuid.uuid4()}",
            "title": str(plan.get("title") or "Proposed Azure infrastructure plan"),
            "operations": plan.get("operations") or [],
            "resources": plan.get("resources") or [],
            "dependencies": str(plan.get("dependencies") or ""),
            "risk_level": str(plan.get("risk_level") or "Low"),
            "estimated_cost_note": plan.get("estimated_cost_note"),
            "critic_verdict": plan.get("critic_verdict"),
            "revision_count": int(plan.get("revision_count") or 0),
            "status": str(plan.get("status") or "pending"),
            "session_id": session_id,
        },
    }


@app.post("/api/agent/stream")
async def agent_stream(request: AgentChatRequest) -> StreamingResponse:
    subscription_id = _resolve_subscription_id(request.subscriptionId)
    session_id = request.sessionId or str(uuid.uuid4())

    async def event_stream():
        queue: asyncio.Queue[dict[str, Any] | None] = asyncio.Queue()

        async def on_tool_event(tool: str, invocation_id: str, phase: str, success: bool | None) -> None:
            activity_id = f"tool-{invocation_id}"
            builder_tools = {
                "create_resource_group",
                "deploy_resource",
                "update_resource",
                "rethink_deployment",
                "delete_resource",
                "delete_resource_group",
                "verify_resource_exists",
                "verify_resource_group_exists",
            }
            tool_agent = (
                Constants.INFRA_BUILDER_AGENT
                if tool in builder_tools
                else
                Constants.INFRA_CRAWLER_AGENT
                if tool.startswith("analyze_")
                else Constants.INFRA_PLANNER_AGENT
                if tool in {"infer_intent", "critic", "brainstorm", "propose_plan", "propose_delete_plan"}
                else Constants.INFRA_READER_AGENT
            )
            if phase == "start":
                await queue.put({"type": "tool_call", "data": {"tool": tool, "session_id": session_id}})
                await queue.put(
                    {
                        "type": "activity_start",
                        "data": {
                            "id": activity_id,
                            "kind": "tool",
                            "tool": tool,
                            "agent": tool_agent,
                            "status": "running",
                            "summary": f"{tool} invoked",
                            "session_id": session_id,
                        },
                    }
                )
                await asyncio.sleep(0)
                return

            await queue.put(
                {
                    "type": "tool_result",
                    "data": {"tool": tool, "success": bool(success), "session_id": session_id},
                }
            )
            await queue.put(
                {
                    "type": "activity_end",
                    "data": {
                        "id": activity_id,
                        "kind": "tool",
                        "tool": tool,
                        "agent": tool_agent,
                        "status": "success" if success else "failed",
                        "summary": f"{tool} done" if success else f"{tool} failed",
                        "session_id": session_id,
                    },
                }
            )

        async def run_agent() -> None:
            try:
                await queue.put(
                    {
                        "type": "activity_start",
                        "data": {
                            "id": f"agent-{session_id}",
                            "kind": Constants.GROUP_CHAT_ACTIVITY_KIND,
                            "agent": None,
                            "status": "running",
                            "summary": "Agents cooking",
                            "session_id": session_id,
                        },
                    }
                )
                await asyncio.sleep(0)
                result = await ask_cloudops_group_chat(
                    request.message,
                    subscription_id=subscription_id,
                    on_tool_event=on_tool_event,
                    session_id=session_id,
                )
                await queue.put(
                    {
                        "type": "activity_end",
                        "data": {
                            "id": f"agent-{session_id}",
                            "kind": Constants.GROUP_CHAT_ACTIVITY_KIND,
                            "agent": None,
                            "status": "success",
                            "summary": "Agents cooking done",
                            "session_id": session_id,
                        },
                    }
                )
                plan_event = _plan_event(result.plan, session_id)
                if plan_event:
                    await queue.put(plan_event)
                await queue.put({"type": "reply", "data": {"content": result.reply, "session_id": session_id}})
            except Exception as exc:
                await queue.put(
                    {
                        "type": "activity_end",
                        "data": {
                            "id": f"agent-{session_id}",
                            "kind": Constants.GROUP_CHAT_ACTIVITY_KIND,
                            "agent": None,
                            "status": "failed",
                            "summary": "Analysis failed",
                            "message": str(exc),
                            "session_id": session_id,
                        },
                    }
                )
                await queue.put({"type": "error", "data": {"message": str(exc), "session_id": session_id}})
            finally:
                await queue.put(None)

        task = asyncio.create_task(run_agent())
        try:
            while True:
                event = await queue.get()
                if event is None:
                    break
                yield _sse_event(event)
        finally:
            await task

    return StreamingResponse(
        event_stream(),
        media_type="text/event-stream",
        headers={
            "Cache-Control": "no-cache",
            "X-Accel-Buffering": "no",
        },
    )


@app.post("/api/agent/plan/{plan_id}/approve")
async def approve_plan(plan_id: str, sessionId: str = Query(default="")) -> dict[str, str | bool]:
    return {"approved": True, "planId": plan_id, "sessionId": sessionId}


@app.post("/api/agent/plan/{plan_id}/reject")
async def reject_plan(plan_id: str) -> dict[str, str | bool]:
    return {"rejected": True, "planId": plan_id}


@app.delete("/api/agent/session/{session_id}")
async def reset_agent_session(session_id: str) -> dict[str, str | bool]:
    reset = await reset_cloudops_group_chat(session_id)
    return {"reset": reset, "sessionId": session_id}


@app.post("/api/agent/question/{question_id}/answer")
async def answer_question(
    question_id: str,
    request: QuestionAnswerRequest,
    sessionId: str = Query(default=""),
) -> dict[str, str | bool]:
    return {
        "answered": True,
        "questionId": question_id,
        "sessionId": sessionId,
        "answer": request.answer,
    }


@app.post("/api/diff")
async def diff(payload: dict[str, Any], subscriptionId: str = Query(default="")) -> dict[str, list[dict[str, object]]]:
    subscription_id = _resolve_subscription_id(subscriptionId)

    if _looks_like_intent(payload):
        raise HTTPException(
            status_code=400,
            detail=(
                "Intent JSON is no longer accepted here. Send a DesiredStateSpec; "
                "intent flows run through the agent and its read tools."
            ),
        )

    try:
        desired = DesiredStateSpec.model_validate(payload)
    except ValidationError as exc:
        raise HTTPException(
            status_code=400, detail=f"Could not parse infrastructure JSON: {exc}") from exc

    live_nodes = await get_infrastructure_nodes(subscription_id)
    return compute_diff(live_nodes, desired)


@app.get("/api/desiredstate")
async def desired_state_get(
    orgUrl: str,
    project: str,
    repository: str,
    pat: str,
    branch: str = "main",
    filePath: str = "infra/desired-state.json",
) -> Response:
    cfg = AzureDevOpsConfig(
        org_url=orgUrl,
        project=project,
        repository=repository,
        pat=pat,
        branch=branch,
        file_path=filePath,
    )
    content = await get_desired_state(cfg)
    if content is None:
        raise HTTPException(
            status_code=404, detail="File not found in repository.")

    return Response(content=content, media_type="application/json")


@app.put("/api/desiredstate")
async def desired_state_put(request: SaveDesiredStateRequest) -> dict[str, bool]:
    cfg = AzureDevOpsConfig(
        org_url=request.orgUrl,
        project=request.project,
        repository=request.repository,
        pat=request.pat,
        branch=request.branch,
        file_path=request.filePath,
    )
    await push_desired_state(
        cfg,
        request.rawJson,
        request.commitMessage or "Update desired infrastructure state",
    )
    return {"committed": True}
