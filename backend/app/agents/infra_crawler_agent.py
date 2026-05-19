from __future__ import annotations

from typing import Any

from semantic_kernel.agents import AzureAIAgent

from app.constants import Constants
from app.plugins.infra_crawler_plugin import InfraCrawlerPlugin
from app.plugins.infra_reader_plugin import InfraReaderPlugin

INFRA_CRAWLER_INSTRUCTIONS = Constants.INFRA_CRAWLER_INSTRUCTIONS


async def create_infra_crawler_definition(client: Any, model_name: str) -> Any:
    return await client.agents.create_agent(
        model=model_name,
        name=Constants.INFRA_CRAWLER_AGENT,
        instructions=INFRA_CRAWLER_INSTRUCTIONS,
    )


def create_infra_crawler_agent(client: Any, definition: Any) -> AzureAIAgent:
    return AzureAIAgent(
        client=client,
        definition=definition,
        plugins=[InfraReaderPlugin(), InfraCrawlerPlugin()],
    )
