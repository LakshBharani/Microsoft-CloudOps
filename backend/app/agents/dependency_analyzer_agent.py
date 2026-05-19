from __future__ import annotations

from typing import Any

from semantic_kernel.agents import AzureAIAgent

from app.constants import DEPENDENCY_ANALYSIS_COMPLETE, DEPENDENCY_ANALYZER_AGENT
from app.plugins.dependency_analyzer_plugin import DependencyAnalyzerPlugin
from app.plugins.infra_analyzer_plugin import InfraAnalyzerPlugin

DEPENDENCY_ANALYZER_INSTRUCTIONS = (
    "You are dependency-analyzer, a read-only Azure dependency analysis agent. "
    "Use the provided tools to inspect resource relationships and explain dependency edges. "
    "Never create, update, delete, deploy, or modify Azure resources. "
    "Classify dependencies as scope, hosting, network, data, observability, service, compute, or generic. "
    f"Finish with the exact phrase: {DEPENDENCY_ANALYSIS_COMPLETE}."
)


async def create_dependency_analyzer_definition(client: Any, model_name: str) -> Any:
    return await client.agents.create_agent(
        model=model_name,
        name=DEPENDENCY_ANALYZER_AGENT,
        instructions=DEPENDENCY_ANALYZER_INSTRUCTIONS,
    )


def create_dependency_analyzer_agent(client: Any, definition: Any) -> AzureAIAgent:
    return AzureAIAgent(
        client=client,
        definition=definition,
        plugins=[InfraAnalyzerPlugin(), DependencyAnalyzerPlugin()],
    )
