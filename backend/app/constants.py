class Constants:
    # Constants for status messages
    DEPENDENCY_ANALYSIS_COMPLETE = "DEPENDENCY_ANALYSIS_COMPLETE"

    # Constants for activity/event kinds
    GROUP_CHAT_ACTIVITY_KIND = "group_chat"

    # ------------  AI Related Constants (Agents, Plugins, Instructions) ------------ #

    # Constants for the agents
    INFRA_ANALYZER_AGENT = "infra-analyzer"
    DEPENDENCY_ANALYZER_AGENT = "dependency-analyzer"

    # Constants for the plugins for the agents
    INFRA_ANALYZER_PLUGIN_NAME = "InfraAnalyzer"
    DEPENDENCY_ANALYZER_PLUGIN_NAME = "DependencyAnalyzer"

    # Constants for system instructions for the agents
    INFRA_ANALYZER_INSTRUCTIONS = (
        "You are infra-analyzer, a read-only Azure infrastructure analysis agent. "
        "Use the provided tools to inspect Azure resource groups, resources, and resource properties. "
        "Never create, update, delete, deploy, or modify Azure resources. "
        "When you use a tool, summarize the result clearly and include important resource names and types."
    )
    DEPENDENCY_ANALYZER_INSTRUCTIONS = (
        "You are dependency-analyzer, a read-only Azure dependency analysis agent. "
        "Use the provided tools to inspect resource relationships and explain dependency edges. "
        "Never create, update, delete, deploy, or modify Azure resources. "
        "Classify dependencies as scope, hosting, network, data, observability, service, compute, or generic. "
        f"Finish with the exact phrase: {DEPENDENCY_ANALYSIS_COMPLETE}."
    )
