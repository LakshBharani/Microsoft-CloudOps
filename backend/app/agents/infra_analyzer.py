
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
from semantic_kernel import Kernel
from semantic_kernel.agents import AzureAIAgent
from semantic_kernel.exceptions.agent_exceptions import AgentInvokeException

from app.plugins.azure_read_plugin import (
    AzureReadPlugin,
    reset_tool_event_handler,
    set_tool_event_handler,
)


load_dotenv(BACKEND_ROOT / ".env")

ToolEventHandler = Callable[[str, str, str, bool | None], Awaitable[None]]
INFRA_ANALYZER = "infra-analyzer"
INFRA_ANALYZER_INSTRUCTIONS = (
    "You are infra-analyzer, a read-only Azure infrastructure analysis agent. "
    "Use the provided tools to inspect Azure resource groups, resources, and resource properties. "
    "Never create, update, delete, deploy, or modify Azure resources. "
    "When you use a tool, summarize the result clearly and include important resource names and types."
)


def normalize_project_endpoint(value: str) -> str:
    value = value.strip().strip('"')
    if value.startswith("http"):
        return value

    parts = [part.strip().strip('"') for part in value.split(";")]
    if len(parts) >= 4:
        os.environ["AZURE_AI_AGENTS_TESTS_IS_TEST_RUN"] = "True"
        return value

    return value


def configure_semantic_kernel_env() -> None:
    project_connection_string = os.environ["AZURE_AI_AGENT_PROJECT_CONNECTION_STRING"]
    os.environ["AZURE_AI_AGENT_ENDPOINT"] = normalize_project_endpoint(
        project_connection_string)


def default_subscription_id() -> str:
    subscription_id = os.environ.get("AZURE_SUBSCRIPTION_ID", "").strip()
    if subscription_id:
        return subscription_id

    connection_string = os.environ.get("AZURE_AI_AGENT_PROJECT_CONNECTION_STRING", "")
    parts = [part.strip() for part in connection_string.split(";")]
    return parts[1] if len(parts) >= 2 else ""


def _kernel_with_agent_tools() -> Kernel:
    kernel = Kernel()
    kernel.add_plugin(AzureReadPlugin(), plugin_name="AzureRead")
    return kernel


def _message_with_subscription(message: str, subscription_id: str) -> str:
    if not subscription_id:
        return message
    return (
        f"{message}\n\n"
        f"Use subscription_id `{subscription_id}` for every Azure read tool call."
    )


async def ask_infra_analyzer(
    message: str,
    subscription_id: str = "",
    on_tool_event: ToolEventHandler | None = None,
) -> str:
    configure_semantic_kernel_env()
    model_name = os.environ["AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME"].strip().strip(
        '"')
    resolved_subscription_id = subscription_id.strip() or default_subscription_id()
    user_query = _message_with_subscription(message, resolved_subscription_id)

    async with (
        DefaultAzureCredential() as creds,
        AzureAIAgent.create_client(credential=creds) as client,
    ):
        agent_definition = await client.agents.create_agent(
            model=model_name,
            name=INFRA_ANALYZER,
            instructions=INFRA_ANALYZER_INSTRUCTIONS,
        )
        print(f"Created agent, agent ID: {agent_definition.id}")

        agent = AzureAIAgent(client=client, definition=agent_definition)
        kernel = _kernel_with_agent_tools()
        handler_token = set_tool_event_handler(on_tool_event)

        try:
            response = await agent.get_response(messages=user_query, kernel=kernel)
            return str(response)
        finally:
            reset_tool_event_handler(handler_token)
            await client.agents.delete_agent(agent_definition.id)
            print(f"Deleted agent, agent ID: {agent_definition.id}")


async def main() -> None:
    subscription_id = default_subscription_id()
    user_query = os.environ.get(
        "AGENT_TEST_QUERY", f"List Azure resource groups in subscription {subscription_id}.")

    print(f"# User: {user_query}")
    try:
        response = await ask_infra_analyzer(user_query, subscription_id)
        print(f"# infra-analyzer: {response}")
    except AgentInvokeException as exc:
        print(f"# Agent run failed: {exc}")


if __name__ == "__main__":
    asyncio.run(main())
