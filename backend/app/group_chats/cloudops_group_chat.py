from __future__ import annotations

import asyncio
import os
import sys
from pathlib import Path
from typing import Awaitable, Callable

BACKEND_ROOT = Path(__file__).resolve().parents[2]
if str(BACKEND_ROOT) not in sys.path:
    sys.path.insert(0, str(BACKEND_ROOT))

from azure.identity.aio import DefaultAzureCredential
from dotenv import load_dotenv
from semantic_kernel.agents import Agent, AgentGroupChat, AzureAIAgent
from semantic_kernel.agents.strategies import SelectionStrategy, TerminationStrategy
from semantic_kernel.contents import ChatMessageContent

from app.agents.dependency_analyzer_agent import (
    create_dependency_analyzer_agent,
    create_dependency_analyzer_definition,
)
from app.agents.infra_analyzer_agent import (
    INFRA_ANALYZER_INSTRUCTIONS,
    _message_with_subscription,
    configure_semantic_kernel_env,
    default_subscription_id,
)
from app.constants import Constants
from app.plugins.dependency_analyzer_plugin import (
    reset_dependency_tool_event_handler,
    set_dependency_tool_event_handler,
)
from app.plugins.infra_analyzer_plugin import (
    InfraAnalyzerPlugin,
    reset_tool_event_handler,
    set_tool_event_handler,
)


load_dotenv(BACKEND_ROOT / ".env")

ToolEventHandler = Callable[[str, str, str, bool | None], Awaitable[None]]

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


def _history_text(history: list[ChatMessageContent]) -> str:
    return "\n".join(str(message.content or "") for message in history).lower()


def _agent_by_name(agents: list[Agent], name: str) -> Agent:
    return next(agent for agent in agents if agent.name == name)


def _needs_dependency_analysis(history: list[ChatMessageContent]) -> bool:
    text = _history_text(history)
    return any(keyword in text for keyword in DEPENDENCY_KEYWORDS)


class CloudOpsSelectionStrategy(SelectionStrategy):
    """Select the next CloudOps agent deterministically."""

    async def select_agent(
        self,
        agents: list[Agent],
        history: list[ChatMessageContent],
    ) -> Agent:
        if not _needs_dependency_analysis(history):
            return _agent_by_name(agents, Constants.INFRA_ANALYZER_AGENT)

        if not self.has_selected:
            return _agent_by_name(agents, Constants.INFRA_ANALYZER_AGENT)

        last_agent_name = history[-1].name if history else ""
        if last_agent_name == Constants.INFRA_ANALYZER_AGENT:
            return _agent_by_name(agents, Constants.DEPENDENCY_ANALYZER_AGENT)

        return _agent_by_name(agents, Constants.INFRA_ANALYZER_AGENT)


class CloudOpsTerminationStrategy(TerminationStrategy):
    """Stop after the selected analysis path has produced an answer."""

    async def should_agent_terminate(
        self,
        agent: Agent,
        history: list[ChatMessageContent],
    ) -> bool:
        if not history:
            return False

        if not _needs_dependency_analysis(history):
            return agent.name == Constants.INFRA_ANALYZER_AGENT

        latest = str(history[-1].content or "").lower()
        return (
            agent.name == Constants.DEPENDENCY_ANALYZER_AGENT
            or Constants.DEPENDENCY_ANALYSIS_COMPLETE.lower() in latest
            or "need more information" in latest
            or "no matching resource" in latest
        )


async def ask_cloudops_group_chat(
    message: str,
    subscription_id: str = "",
    on_tool_event: ToolEventHandler | None = None,
) -> str:
    configure_semantic_kernel_env()
    model_name = os.environ["AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME"].strip().strip('"')
    resolved_subscription_id = subscription_id.strip() or default_subscription_id()
    user_query = _message_with_subscription(message, resolved_subscription_id)

    async with (
        DefaultAzureCredential() as creds,
        AzureAIAgent.create_client(credential=creds) as client,
    ):
        infra_analyzer_agent_definition = await client.agents.create_agent(
            model=model_name,
            name=Constants.INFRA_ANALYZER_AGENT,
            instructions=INFRA_ANALYZER_INSTRUCTIONS,
        )
        dependency_analyzer_agent_definition = await create_dependency_analyzer_definition(
            client,
            model_name,
        )
        print(
            f"Created agent [{Constants.INFRA_ANALYZER_AGENT}], agent ID: "
            f"{infra_analyzer_agent_definition.id}"
        )
        print(
            f"Created agent [{Constants.DEPENDENCY_ANALYZER_AGENT}], agent ID: "
            f"{dependency_analyzer_agent_definition.id}"
        )

        agent_infra_analyzer = AzureAIAgent(
            client=client,
            definition=infra_analyzer_agent_definition,
            plugins=[InfraAnalyzerPlugin()],
        )
        agent_dependency_analyzer = create_dependency_analyzer_agent(
            client,
            dependency_analyzer_agent_definition,
        )

        chat = AgentGroupChat(
            agents=[agent_infra_analyzer, agent_dependency_analyzer],
            termination_strategy=CloudOpsTerminationStrategy(
                agents=[agent_infra_analyzer, agent_dependency_analyzer],
                maximum_iterations=4,
                automatic_reset=True,
            ),
            selection_strategy=CloudOpsSelectionStrategy(
                agents=[agent_infra_analyzer, agent_dependency_analyzer],
            ),
        )
        await chat.add_chat_message(user_query)

        read_handler_token = set_tool_event_handler(on_tool_event)
        dependency_handler_token = set_dependency_tool_event_handler(on_tool_event)
        try:
            responses: list[str] = []
            async for response in chat.invoke():
                if response.name:
                    responses.append(f"{response.name}: {response.content}")
                else:
                    responses.append(str(response.content))
            return "\n\n".join(responses)
        finally:
            reset_tool_event_handler(read_handler_token)
            reset_dependency_tool_event_handler(dependency_handler_token)
            await client.agents.delete_agent(infra_analyzer_agent_definition.id)
            await client.agents.delete_agent(dependency_analyzer_agent_definition.id)
            print(
                f"Deleted agent [{Constants.INFRA_ANALYZER_AGENT}], agent ID: "
                f"{infra_analyzer_agent_definition.id}"
            )
            print(
                f"Deleted agent [{Constants.DEPENDENCY_ANALYZER_AGENT}], agent ID: "
                f"{dependency_analyzer_agent_definition.id}"
            )


async def main() -> None:
    subscription_id = default_subscription_id()
    user_query = os.environ.get(
        "AGENT_TEST_QUERY",
        f"List Azure resource groups in subscription {subscription_id}.",
    )
    print(f"# User: {user_query}")
    response = await ask_cloudops_group_chat(user_query, subscription_id)
    print(response)


if __name__ == "__main__":
    asyncio.run(main())
