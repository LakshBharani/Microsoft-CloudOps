from __future__ import annotations

from typing import Any

from semantic_kernel.agents import AzureAIAgent

from app.constants import Constants
from app.plugins.infra_planner_plugin import InfraPlannerPlugin

INFRA_PLANNER_INSTRUCTIONS = Constants.INFRA_PLANNER_INSTRUCTIONS


async def create_infra_planner_definition(client: Any, model_name: str) -> Any:
    return await client.agents.create_agent(
        model=model_name,
        name=Constants.INFRA_PLANNER_AGENT,
        instructions=INFRA_PLANNER_INSTRUCTIONS,
    )


def create_infra_planner_agent(client: Any, definition: Any) -> AzureAIAgent:
    return AzureAIAgent(
        client=client,
        definition=definition,
        plugins=[InfraPlannerPlugin()],
    )
