from __future__ import annotations

from typing import Any

from semantic_kernel.agents import AzureAIAgent

from app.plugins.azure_dependency_plugin import AzureDependencyPlugin
from app.plugins.azure_read_plugin import AzureReadPlugin

DEPENDENCY_ANALYZER = "dependency-analyzer"
DEPENDENCY_ANALYZER_INSTRUCTIONS = (
    "You are dependency-analyzer, a read-only Azure dependency analysis agent. "
    "Use the provided tools to inspect resource relationships and explain dependency edges. "
    "Never create, update, delete, deploy, or modify Azure resources. "
    "Classify dependencies as scope, hosting, network, data, observability, service, compute, or generic. "
    "Finish with the exact phrase: DEPENDENCY_ANALYSIS_COMPLETE."
)


async def create_dependency_analyzer_definition(client: Any, model_name: str) -> Any:
    return await client.agents.create_agent(
        model=model_name,
        name=DEPENDENCY_ANALYZER,
        instructions=DEPENDENCY_ANALYZER_INSTRUCTIONS,
    )


def create_dependency_analyzer_agent(client: Any, definition: Any) -> AzureAIAgent:
    return AzureAIAgent(
        client=client,
        definition=definition,
        plugins=[AzureReadPlugin(), AzureDependencyPlugin()],
    )
