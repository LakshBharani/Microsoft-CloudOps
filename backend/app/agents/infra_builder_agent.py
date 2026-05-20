from __future__ import annotations

from typing import Any

from semantic_kernel.agents import AzureAIAgent

from app.constants import Constants
from app.plugins.infra_builder_plugin import InfraBuilderPlugin

INFRA_BUILDER_INSTRUCTIONS = Constants.INFRA_BUILDER_INSTRUCTIONS


async def create_infra_builder_definition(client: Any, model_name: str) -> Any:
    return await client.agents.create_agent(
        model=model_name,
        name=Constants.INFRA_BUILDER_AGENT,
        instructions=INFRA_BUILDER_INSTRUCTIONS,
    )


def create_infra_builder_agent(client: Any, definition: Any) -> AzureAIAgent:
    return AzureAIAgent(
        client=client,
        definition=definition,
        plugins=[InfraBuilderPlugin()],
    )
