from __future__ import annotations

from typing import Any

from semantic_kernel.agents import AzureAIAgent

from app.constants import Constants
from app.plugins.dependency_analyzer_plugin import DependencyAnalyzerPlugin
from app.plugins.infra_analyzer_plugin import InfraAnalyzerPlugin

DEPENDENCY_ANALYZER_INSTRUCTIONS = Constants.DEPENDENCY_ANALYZER_INSTRUCTIONS


async def create_dependency_analyzer_definition(client: Any, model_name: str) -> Any:
    return await client.agents.create_agent(
        model=model_name,
        name=Constants.DEPENDENCY_ANALYZER_AGENT,
        instructions=DEPENDENCY_ANALYZER_INSTRUCTIONS,
    )


def create_dependency_analyzer_agent(client: Any, definition: Any) -> AzureAIAgent:
    return AzureAIAgent(
        client=client,
        definition=definition,
        plugins=[InfraAnalyzerPlugin(), DependencyAnalyzerPlugin()],
    )
