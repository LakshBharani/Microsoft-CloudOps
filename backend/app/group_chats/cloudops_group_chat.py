from __future__ import annotations
from dataclasses import dataclass
from app.plugins.infra_reader_plugin import (
    InfraReaderPlugin,
    reset_infra_reader_tool_event_handler,
    set_infra_reader_tool_event_handler,
)
from app.plugins.infra_planner_plugin import (
    get_infra_planner_plan,
    reset_infra_planner_plan,
    reset_infra_planner_tool_event_handler,
    set_infra_planner_plan,
    set_infra_planner_tool_event_handler,
)
from app.plugins.infra_crawler_plugin import (
    reset_infra_crawler_tool_event_handler,
    set_infra_crawler_tool_event_handler,
)
from app.plugins.infra_builder_plugin import (
    reset_infra_builder_tool_event_handler,
    set_infra_builder_tool_event_handler,
)
from app.constants import Constants
from app.agents.infra_planner_agent import (
    create_infra_planner_agent,
    create_infra_planner_definition,
)
from app.agents.infra_reader_agent import (
    INFRA_READER_INSTRUCTIONS,
    _message_with_subscription,
    configure_semantic_kernel_env,
    default_subscription_id,
)
from app.agents.infra_crawler_agent import (
    create_infra_crawler_agent,
    create_infra_crawler_definition,
)
from app.agents.infra_builder_agent import (
    create_infra_builder_agent,
    create_infra_builder_definition,
)
from semantic_kernel.contents import ChatMessageContent
from semantic_kernel.agents.strategies import SelectionStrategy, TerminationStrategy
from semantic_kernel.agents import Agent, AgentGroupChat, AzureAIAgent
from dotenv import load_dotenv
from azure.identity.aio import DefaultAzureCredential

import asyncio
import json
import os
import re
import sys
from pathlib import Path
from typing import Any, Awaitable, Callable

BACKEND_ROOT = Path(__file__).resolve().parents[2]
if str(BACKEND_ROOT) not in sys.path:
    sys.path.insert(0, str(BACKEND_ROOT))


load_dotenv(BACKEND_ROOT / ".env")

ToolEventHandler = Callable[[str, str, str, bool | None], Awaitable[None]]


@dataclass
class CloudOpsGroupChatResult:
    reply: str
    plan: dict[str, Any] | None = None


DEPENDENCY_KEYWORDS = (
    "depend",
    "dependency",
    "dependencies",
    "depends",
    "impact",
    "relationship",
    "relationships",
    "connected",
    "connections",
    "edge",
    "edges",
)

INFRA_PLANNER_KEYWORDS = (
    "add",
    "infra-planner",
    "architecture",
    "build",
    "change",
    "configure",
    "create",
    "delete",
    "decommission",
    "deploy",
    "design",
    "desired state",
    "draft",
    "migrate",
    "modify",
    "plan",
    "propose",
    "provision",
    "remove",
    "setup",
    "target state",
    "update",
)

EXECUTION_KEYWORDS = (
    "apply approved",
    "approved plan",
    "deploy approved",
    "execute approved",
    "execute plan",
    "run plan",
)

JSON_PLAN_PATTERN = re.compile(
    r"<json>\s*(?P<json>\{.*?\})\s*</json>",
    re.IGNORECASE | re.DOTALL,
)


def _request_text(history: list[ChatMessageContent]) -> str:
    if not history:
        return ""
    for message in reversed(history):
        if not message.name:
            return str(message.content or "").lower()
    return str(history[-1].content or "").lower()


def _agent_by_name(agents: list[Agent], name: str) -> Agent:
    return next(agent for agent in agents if agent.name == name)


def _needs_dependency_analysis(history: list[ChatMessageContent]) -> bool:
    text = _request_text(history)
    return any(keyword in text for keyword in DEPENDENCY_KEYWORDS)


def _needs_architecture_plan(history: list[ChatMessageContent]) -> bool:
    text = _request_text(history)
    return any(keyword in text for keyword in INFRA_PLANNER_KEYWORDS)


def _needs_execution(history: list[ChatMessageContent]) -> bool:
    text = _request_text(history)
    return any(keyword in text for keyword in EXECUTION_KEYWORDS)


def _text_needs_architecture_plan(text: str) -> bool:
    normalized = text.lower()
    return any(keyword in normalized for keyword in INFRA_PLANNER_KEYWORDS)


def _is_approved_plan_execution(text: str) -> bool:
    normalized = text.lower()
    return "execute approved plan" in normalized and "approved plan json" in normalized


def _verification_prompt(user_query: str, builder_text: str, subscription_id: str) -> str:
    return (
        "Verify the approved infrastructure operation that just ran. "
        "You are in read-only verification mode: do not plan, propose, create, update, or delete. "
        "Use reader tools to inspect Azure state against the Approved plan JSON. "
        "For Create/Update/Deploy operations, verify resources exist and key properties if available. "
        "For Delete operations, verify resources or resource groups are absent. "
        "Azure Resource Graph can lag; if the builder reports delete accepted for a resource group, "
        "treat that as acceptable unless live inventory clearly contradicts it. "
        "End with exactly one line: VERIFICATION_STATUS: success or VERIFICATION_STATUS: mismatch. "
        "If mismatch, list only the concrete remaining gaps.\n\n"
        f"Subscription: {subscription_id}\n\n"
        f"Approved execution request:\n{user_query}\n\n"
        f"Builder result:\n{builder_text}"
    )


def _verification_needs_repair(reader_text: str) -> bool:
    normalized = reader_text.lower()
    if "verification_status: success" in normalized:
        return False
    if "verification_status: mismatch" in normalized:
        return True
    repair_markers = (
        "missing",
        "not found",
        "still exists",
        "failed",
        "mismatch",
        "does not match",
        "not achieved",
    )
    return any(marker in normalized for marker in repair_markers)


def _repair_plan_prompt(user_query: str, builder_text: str, reader_text: str) -> str:
    return (
        "The approved infrastructure operation was executed, then verification found remaining gaps. "
        "Create exactly one repair plan for only the missing or incorrect pieces. "
        "Do not repeat successful operations. Do not execute anything. "
        "Use the same JSON plan-card contract as normal planning.\n\n"
        f"Original approved execution request:\n{user_query}\n\n"
        f"Builder result:\n{builder_text}\n\n"
        f"Reader verification:\n{reader_text}"
    )


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
    return str(value or "Update").strip() or "Update"


def _normalize_risk(value: Any) -> str:
    risk = str(value or "").strip().lower()
    if risk == "high":
        return "High"
    if risk == "medium":
        return "Medium"
    return "Low"


def _normalize_plan_operation(item: Any, index: int) -> dict[str, Any] | None:
    if not isinstance(item, dict):
        return None

    resource_type = str(
        item.get("resource_type") or item.get("resourceType") or ""
    ).strip()
    resource_name = str(
        item.get("resource_name") or item.get("resourceName") or ""
    ).strip()
    if not resource_type or not resource_name:
        return None

    operation: dict[str, Any] = {
        "action": _normalize_action(item.get("action")),
        "resource_type": resource_type,
        "resource_name": resource_name,
        "details": item.get("details") or item.get("description") or f"Step {index}",
    }
    resource_group = item.get("resource_group") or item.get("resourceGroup")
    if resource_group:
        operation["resource_group"] = str(resource_group).strip()
    return operation


def _normalize_plan(plan: Any) -> dict[str, Any] | None:
    if not isinstance(plan, dict):
        return None

    operations = [
        operation
        for index, item in enumerate(plan.get("operations") or [], start=1)
        if (operation := _normalize_plan_operation(item, index)) is not None
    ]
    if not operations:
        return None

    return {
        "title": str(plan.get("title") or "Proposed Azure infrastructure plan"),
        "summary": str(plan.get("summary") or ""),
        "operations": operations,
        "resources": plan.get("resources") or [
            {
                "action": operation["action"],
                "resource_type": operation["resource_type"],
                "resource_name": operation["resource_name"],
                **(
                    {"resource_group": operation["resource_group"]}
                    if operation.get("resource_group")
                    else {}
                ),
            }
            for operation in operations
        ],
        "dependencies": str(plan.get("dependencies") or ""),
        "risk_level": _normalize_risk(plan.get("risk_level") or plan.get("riskLevel")),
        "estimated_cost_note": plan.get("estimated_cost_note")
        or plan.get("estimatedCostNote")
        or "Cost estimate is not calculated yet.",
        "critic_verdict": plan.get("critic_verdict")
        or plan.get("criticVerdict")
        or "Draft plan only. No Azure changes will be made until approved.",
        "revision_count": int(plan.get("revision_count") or plan.get("revisionCount") or 0),
        "status": str(plan.get("status") or "pending"),
    }


def _extract_tagged_plan(text: str) -> dict[str, Any] | None:
    match = JSON_PLAN_PATTERN.search(text)
    if not match:
        return None

    try:
        parsed = json.loads(match.group("json"))
    except json.JSONDecodeError:
        return None
    return _normalize_plan(parsed)


def _strip_tagged_plan(text: str) -> str:
    stripped = JSON_PLAN_PATTERN.sub("", text)
    stripped = stripped.replace(Constants.INFRA_PLANNER_PLAN_COMPLETE, "")
    stripped = re.sub(r"\n{3,}", "\n\n", stripped)
    return stripped.strip()


async def _retry_infra_planner_plan_tags(
    agent_infra_planner: AzureAIAgent,
    reply: str,
) -> dict[str, Any] | None:
    retry_prompt = (
        "Rewrite the final architecture plan from the previous response as a single "
        "valid JSON object wrapped exactly in <json> and </json> tags. "
        "Do not include markdown fences or extra prose. "
        "Use these fields: title, summary, operations, risk_level, "
        "estimated_cost_note, critic_verdict. "
        "Each operation must include action, resource_type, resource_name, details, "
        "and optional resource_group. Previous response:\n"
        f"{reply}"
    )
    response = await agent_infra_planner.get_response(messages=retry_prompt)
    return _extract_tagged_plan(str(response)) or get_infra_planner_plan()


class CloudOpsSelectionStrategy(SelectionStrategy):
    """Select the next CloudOps agent deterministically."""

    async def select_agent(
        self,
        agents: list[Agent],
        history: list[ChatMessageContent],
    ) -> Agent:
        needs_architecture = _needs_architecture_plan(history)
        needs_dependency = _needs_dependency_analysis(history)
        needs_execution = _needs_execution(history)

        if needs_execution:
            return _agent_by_name(agents, Constants.INFRA_BUILDER_AGENT)

        if not needs_architecture and not needs_dependency:
            return _agent_by_name(agents, Constants.INFRA_READER_AGENT)

        if not self.has_selected:
            return _agent_by_name(agents, Constants.INFRA_READER_AGENT)

        last_agent_name = history[-1].name if history else ""
        if last_agent_name == Constants.INFRA_READER_AGENT and needs_dependency:
            return _agent_by_name(agents, Constants.INFRA_CRAWLER_AGENT)

        if needs_architecture and last_agent_name in {
            Constants.INFRA_READER_AGENT,
            Constants.INFRA_CRAWLER_AGENT,
        }:
            return _agent_by_name(agents, Constants.INFRA_PLANNER_AGENT)

        return _agent_by_name(agents, Constants.INFRA_READER_AGENT)


class CloudOpsTerminationStrategy(TerminationStrategy):
    """Stop after the selected analysis path has produced an answer."""

    async def should_agent_terminate(
        self,
        agent: Agent,
        history: list[ChatMessageContent],
    ) -> bool:
        if not history:
            return False

        needs_architecture = _needs_architecture_plan(history)
        needs_dependency = _needs_dependency_analysis(history)
        needs_execution = _needs_execution(history)

        if needs_execution:
            return agent.name == Constants.INFRA_BUILDER_AGENT

        if not needs_architecture and not needs_dependency:
            return agent.name == Constants.INFRA_READER_AGENT

        if needs_architecture:
            latest = str(history[-1].content or "").lower()
            return (
                agent.name == Constants.INFRA_PLANNER_AGENT
                or Constants.INFRA_PLANNER_PLAN_COMPLETE.lower() in latest
                or "need more information" in latest
            )

        latest = str(history[-1].content or "").lower()
        return (
            agent.name == Constants.INFRA_CRAWLER_AGENT
            or Constants.INFRA_CRAWLER_ANALYSIS_COMPLETE.lower() in latest
            or "need more information" in latest
            or "no matching resource" in latest
        )


async def ask_cloudops_group_chat(
    message: str,
    subscription_id: str = "",
    on_tool_event: ToolEventHandler | None = None,
    session_id: str = "cloudops-ui-session",
) -> CloudOpsGroupChatResult:
    configure_semantic_kernel_env()
    resolved_subscription_id = subscription_id.strip() or default_subscription_id()
    session = await _get_or_create_active_group_chat(session_id, resolved_subscription_id)
    return await session.ask(message, resolved_subscription_id, on_tool_event)


@dataclass
class _CloudOpsGroupChatSession:
    session_id: str
    credential: DefaultAzureCredential
    client_context: Any
    client: Any
    infra_reader_definition: Any
    infra_crawler_definition: Any
    infra_planner_definition: Any
    infra_builder_definition: Any
    agent_infra_reader: AzureAIAgent
    agent_infra_planner: AzureAIAgent
    agent_infra_builder: AzureAIAgent
    chat: AgentGroupChat
    lock: asyncio.Lock

    @classmethod
    async def create(cls, session_id: str, subscription_id: str) -> "_CloudOpsGroupChatSession":
        configure_semantic_kernel_env()
        model_name = os.environ["AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME"].strip().strip(
            '"')
        credential = DefaultAzureCredential()
        entered_credential = await credential.__aenter__()
        client_context = AzureAIAgent.create_client(credential=entered_credential)
        client = await client_context.__aenter__()
        created_definitions: list[tuple[str, Any]] = []

        try:
            infra_reader_definition = await client.agents.create_agent(
                model=model_name,
                name=Constants.INFRA_READER_AGENT,
                instructions=INFRA_READER_INSTRUCTIONS,
            )
            created_definitions.append((Constants.INFRA_READER_AGENT, infra_reader_definition))
            infra_crawler_definition = await create_infra_crawler_definition(
                client,
                model_name,
            )
            created_definitions.append((Constants.INFRA_CRAWLER_AGENT, infra_crawler_definition))
            infra_planner_definition = await create_infra_planner_definition(
                client,
                model_name,
            )
            created_definitions.append((Constants.INFRA_PLANNER_AGENT, infra_planner_definition))
            infra_builder_definition = await create_infra_builder_definition(
                client,
                model_name,
            )
            created_definitions.append((Constants.INFRA_BUILDER_AGENT, infra_builder_definition))

            print(
                f"Created agent [{Constants.INFRA_READER_AGENT}], agent ID: "
                f"{infra_reader_definition.id}"
            )
            print(
                f"Created agent [{Constants.INFRA_CRAWLER_AGENT}], agent ID: "
                f"{infra_crawler_definition.id}"
            )
            print(
                f"Created agent [{Constants.INFRA_PLANNER_AGENT}], agent ID: "
                f"{infra_planner_definition.id}"
            )
            print(
                f"Created agent [{Constants.INFRA_BUILDER_AGENT}], agent ID: "
                f"{infra_builder_definition.id}"
            )

            agent_infra_reader = AzureAIAgent(
                client=client,
                definition=infra_reader_definition,
                plugins=[InfraReaderPlugin()],
            )
            agent_infra_crawler = create_infra_crawler_agent(
                client,
                infra_crawler_definition,
            )
            agent_infra_planner = create_infra_planner_agent(
                client,
                infra_planner_definition,
            )
            agent_infra_builder = create_infra_builder_agent(
                client,
                infra_builder_definition,
            )

            chat = AgentGroupChat(
                agents=[
                    agent_infra_reader,
                    agent_infra_crawler,
                    agent_infra_planner,
                    agent_infra_builder,
                ],
                termination_strategy=CloudOpsTerminationStrategy(
                    agents=[
                        agent_infra_reader,
                        agent_infra_crawler,
                        agent_infra_planner,
                        agent_infra_builder,
                    ],
                    maximum_iterations=6,
                    automatic_reset=True,
                ),
                selection_strategy=CloudOpsSelectionStrategy(
                    agents=[
                        agent_infra_reader,
                        agent_infra_crawler,
                        agent_infra_planner,
                        agent_infra_builder,
                    ],
                ),
            )
            return cls(
                session_id=session_id,
                credential=credential,
                client_context=client_context,
                client=client,
                infra_reader_definition=infra_reader_definition,
                infra_crawler_definition=infra_crawler_definition,
                infra_planner_definition=infra_planner_definition,
                infra_builder_definition=infra_builder_definition,
                agent_infra_reader=agent_infra_reader,
                agent_infra_planner=agent_infra_planner,
                agent_infra_builder=agent_infra_builder,
                chat=chat,
                lock=asyncio.Lock(),
            )
        except Exception:
            for agent_name, definition in reversed(created_definitions):
                try:
                    await client.agents.delete_agent(definition.id)
                except Exception as exc:
                    print(
                        f"Failed to clean up agent [{agent_name}], agent ID: "
                        f"{definition.id}: {exc}"
                    )
            await client_context.__aexit__(None, None, None)
            await credential.__aexit__(None, None, None)
            raise

    async def ask(
        self,
        message: str,
        subscription_id: str,
        on_tool_event: ToolEventHandler | None,
    ) -> CloudOpsGroupChatResult:
        user_query = _message_with_subscription(message, subscription_id)
        async with self.lock:
            if _is_approved_plan_execution(message):
                return await self._execute_and_verify(
                    user_query,
                    subscription_id,
                    on_tool_event,
                )

            await self.chat.add_chat_message(user_query)

            read_handler_token = set_infra_reader_tool_event_handler(on_tool_event)
            infra_crawler_handler_token = set_infra_crawler_tool_event_handler(
                on_tool_event)
            infra_planner_handler_token = set_infra_planner_tool_event_handler(
                on_tool_event)
            infra_builder_handler_token = set_infra_builder_tool_event_handler(
                on_tool_event)
            infra_planner_plan_token = set_infra_planner_plan(None)
            try:
                responses: list[str] = []
                async for response in self.chat.invoke():
                    if response.name:
                        responses.append(f"{response.name}: {response.content}")
                    else:
                        responses.append(str(response.content))

                reply = "\n\n".join(responses)
                plan = get_infra_planner_plan()
                if not plan:
                    plan = _extract_tagged_plan(reply)
                if not plan and _text_needs_architecture_plan(message):
                    plan = await _retry_infra_planner_plan_tags(
                        self.agent_infra_planner,
                        reply,
                    )

                return CloudOpsGroupChatResult(
                    reply=_strip_tagged_plan(reply),
                    plan=plan,
                )
            finally:
                reset_infra_reader_tool_event_handler(read_handler_token)
                reset_infra_crawler_tool_event_handler(
                    infra_crawler_handler_token)
                reset_infra_planner_tool_event_handler(infra_planner_handler_token)
                reset_infra_builder_tool_event_handler(infra_builder_handler_token)
                reset_infra_planner_plan(infra_planner_plan_token)

    async def _execute_and_verify(
        self,
        user_query: str,
        subscription_id: str,
        on_tool_event: ToolEventHandler | None,
    ) -> CloudOpsGroupChatResult:
        await self.chat.add_chat_message(user_query)

        read_handler_token = set_infra_reader_tool_event_handler(on_tool_event)
        infra_planner_handler_token = set_infra_planner_tool_event_handler(
            on_tool_event)
        infra_builder_handler_token = set_infra_builder_tool_event_handler(
            on_tool_event)
        infra_planner_plan_token = set_infra_planner_plan(None)
        try:
            builder_response = await self.agent_infra_builder.get_response(
                messages=user_query,
            )
            builder_text = str(builder_response)

            verification_prompt = _verification_prompt(
                user_query,
                builder_text,
                subscription_id,
            )
            reader_response = await self.agent_infra_reader.get_response(
                messages=verification_prompt,
            )
            reader_text = str(reader_response)

            reply_parts = [
                f"{Constants.INFRA_BUILDER_AGENT}: {builder_text}",
                f"{Constants.INFRA_READER_AGENT}: {reader_text}",
            ]

            if _verification_needs_repair(reader_text):
                repair_prompt = _repair_plan_prompt(
                    user_query,
                    builder_text,
                    reader_text,
                )
                planner_response = await self.agent_infra_planner.get_response(
                    messages=repair_prompt,
                )
                planner_text = str(planner_response)
                reply_parts.append(f"{Constants.INFRA_PLANNER_AGENT}: {planner_text}")

            reply = "\n\n".join(reply_parts)
            plan = get_infra_planner_plan() or _extract_tagged_plan(reply)
            return CloudOpsGroupChatResult(
                reply=_strip_tagged_plan(reply),
                plan=plan,
            )
        finally:
            reset_infra_reader_tool_event_handler(read_handler_token)
            reset_infra_planner_tool_event_handler(infra_planner_handler_token)
            reset_infra_builder_tool_event_handler(infra_builder_handler_token)
            reset_infra_planner_plan(infra_planner_plan_token)

    async def close(self) -> None:
        async with self.lock:
            definitions = (
                (Constants.INFRA_READER_AGENT, self.infra_reader_definition),
                (Constants.INFRA_CRAWLER_AGENT, self.infra_crawler_definition),
                (Constants.INFRA_PLANNER_AGENT, self.infra_planner_definition),
                (Constants.INFRA_BUILDER_AGENT, self.infra_builder_definition),
            )
            for agent_name, definition in definitions:
                try:
                    await self.client.agents.delete_agent(definition.id)
                    print(
                        f"Deleted agent [{agent_name}], agent ID: "
                        f"{definition.id}"
                    )
                except Exception as exc:
                    print(
                        f"Failed to delete agent [{agent_name}], agent ID: "
                        f"{definition.id}: {exc}"
                    )
            await self.client_context.__aexit__(None, None, None)
            await self.credential.__aexit__(None, None, None)


_active_group_chat: _CloudOpsGroupChatSession | None = None
_active_group_chat_lock = asyncio.Lock()


async def _get_or_create_active_group_chat(
    session_id: str,
    subscription_id: str,
) -> _CloudOpsGroupChatSession:
    global _active_group_chat
    async with _active_group_chat_lock:
        if _active_group_chat and _active_group_chat.session_id == session_id:
            return _active_group_chat
        if _active_group_chat:
            await _active_group_chat.close()
        _active_group_chat = await _CloudOpsGroupChatSession.create(
            session_id,
            subscription_id,
        )
        return _active_group_chat


async def reset_cloudops_group_chat(session_id: str = "") -> bool:
    global _active_group_chat
    async with _active_group_chat_lock:
        if not _active_group_chat:
            return False
        if session_id and _active_group_chat.session_id != session_id:
            return False
        await _active_group_chat.close()
        _active_group_chat = None
        return True


async def main() -> None:
    subscription_id = default_subscription_id()
    user_query = os.environ.get(
        "AGENT_TEST_QUERY",
        f"List Azure resource groups in subscription {subscription_id}.",
    )
    print(f"# User: {user_query}")
    response = await ask_cloudops_group_chat(user_query, subscription_id)
    print(response.reply)
    if response.plan:
        print(response.plan)


if __name__ == "__main__":
    asyncio.run(main())
