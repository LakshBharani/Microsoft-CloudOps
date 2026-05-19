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
    "deploy",
    "design",
    "desired state",
    "draft",
    "migrate",
    "modify",
    "plan",
    "propose",
    "provision",
    "setup",
    "target state",
    "update",
)

JSON_PLAN_PATTERN = re.compile(
    r"<json>\s*(?P<json>\{.*?\})\s*</json>",
    re.IGNORECASE | re.DOTALL,
)


def _request_text(history: list[ChatMessageContent]) -> str:
    if not history:
        return ""
    return str(history[0].content or "").lower()


def _agent_by_name(agents: list[Agent], name: str) -> Agent:
    return next(agent for agent in agents if agent.name == name)


def _needs_dependency_analysis(history: list[ChatMessageContent]) -> bool:
    text = _request_text(history)
    return any(keyword in text for keyword in DEPENDENCY_KEYWORDS)


def _needs_architecture_plan(history: list[ChatMessageContent]) -> bool:
    text = _request_text(history)
    return any(keyword in text for keyword in INFRA_PLANNER_KEYWORDS)


def _text_needs_architecture_plan(text: str) -> bool:
    normalized = text.lower()
    return any(keyword in normalized for keyword in INFRA_PLANNER_KEYWORDS)


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
) -> CloudOpsGroupChatResult:
    configure_semantic_kernel_env()
    model_name = os.environ["AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME"].strip().strip(
        '"')
    resolved_subscription_id = subscription_id.strip() or default_subscription_id()
    user_query = _message_with_subscription(message, resolved_subscription_id)

    async with (
        DefaultAzureCredential() as creds,
        AzureAIAgent.create_client(credential=creds) as client,
    ):
        infra_reader_definition = await client.agents.create_agent(
            model=model_name,
            name=Constants.INFRA_READER_AGENT,
            instructions=INFRA_READER_INSTRUCTIONS,
        )
        infra_crawler_definition = await create_infra_crawler_definition(
            client,
            model_name,
        )
        infra_planner_definition = await create_infra_planner_definition(
            client,
            model_name,
        )
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

        chat = AgentGroupChat(
            agents=[agent_infra_reader,
                    agent_infra_crawler, agent_infra_planner],
            termination_strategy=CloudOpsTerminationStrategy(
                agents=[agent_infra_reader,
                        agent_infra_crawler, agent_infra_planner],
                maximum_iterations=6,
                automatic_reset=True,
            ),
            selection_strategy=CloudOpsSelectionStrategy(
                agents=[agent_infra_reader,
                        agent_infra_crawler, agent_infra_planner],
            ),
        )
        await chat.add_chat_message(user_query)

        read_handler_token = set_infra_reader_tool_event_handler(on_tool_event)
        infra_crawler_handler_token = set_infra_crawler_tool_event_handler(
            on_tool_event)
        infra_planner_handler_token = set_infra_planner_tool_event_handler(
            on_tool_event)
        infra_planner_plan_token = set_infra_planner_plan(None)
        try:
            responses: list[str] = []
            async for response in chat.invoke():
                if response.name:
                    responses.append(f"{response.name}: {response.content}")
                else:
                    responses.append(str(response.content))

            reply = "\n\n".join(responses)
            plan = get_infra_planner_plan()
            if not plan:
                plan = _extract_tagged_plan(reply)
            if not plan and _text_needs_architecture_plan(message):
                plan = await _retry_infra_planner_plan_tags(agent_infra_planner, reply)

            return CloudOpsGroupChatResult(
                reply=_strip_tagged_plan(reply),
                plan=plan,
            )
        finally:
            reset_infra_reader_tool_event_handler(read_handler_token)
            reset_infra_crawler_tool_event_handler(
                infra_crawler_handler_token)
            reset_infra_planner_tool_event_handler(infra_planner_handler_token)
            reset_infra_planner_plan(infra_planner_plan_token)
            await client.agents.delete_agent(infra_reader_definition.id)
            await client.agents.delete_agent(infra_crawler_definition.id)
            await client.agents.delete_agent(infra_planner_definition.id)
            print(
                f"Deleted agent [{Constants.INFRA_READER_AGENT}], agent ID: "
                f"{infra_reader_definition.id}"
            )
            print(
                f"Deleted agent [{Constants.INFRA_CRAWLER_AGENT}], agent ID: "
                f"{infra_crawler_definition.id}"
            )
            print(
                f"Deleted agent [{Constants.INFRA_PLANNER_AGENT}], agent ID: "
                f"{infra_planner_definition.id}"
            )


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
