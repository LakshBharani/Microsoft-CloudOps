# InfraIQ

**AI agent system for managing Azure cloud infrastructure through natural language.**

Chat with a team of specialized AI agents to inspect, plan, and deploy Azure resources — with a live infrastructure graph and plan-approve safety flow.

## Demo

<!-- Upload your video here -->


https://github.com/user-attachments/assets/fdb32ea8-f9be-4583-89be-6123a4cc5341





---

## What I Built

A **multi-agent AI system** where four specialized agents collaborate to handle cloud infrastructure requests end-to-end:

| Agent | Responsibility |
|-------|---------------|
| **InfraPlanner** | Understands intent, proposes an infrastructure plan, runs a critic loop to self-review before presenting |
| **InfraReader** | Queries live Azure resource state |
| **InfraCrawler** | Analyzes resource topology, dependencies, and relationships |
| **InfraBuilder** | Executes creates, updates, and deletes against Azure |

The agents communicate through an **orchestrated group chat** (Semantic Kernel). When you send a message, the planner infers intent → proposes a plan → critic reviews it → you approve → builder executes. Every step streams back to the UI in real time via SSE.

```
User message
    │
    ▼
InfraPlanner  ──→  proposes plan  ──→  critic loop
    │
    ▼ (you approve)
InfraBuilder  ──→  Azure Resource Manager
```

## Key Features

- **Natural language infra management** — *"Add a storage account in East US with geo-redundant backup"*
- **Live infrastructure graph** — visual map of your Azure subscription, built with React Flow
- **Plan & approve flow** — agents never touch resources without showing you a plan first
- **Desired state diffing** — compare what you want vs what exists in Azure
- **Azure DevOps integration** — desired-state JSON lives in your repo; changes are committed automatically

## Stack

**Backend** — Python, FastAPI, Semantic Kernel, Azure AI Agents SDK, Azure AI Foundry (GPT-4.1)

**Frontend** — Next.js, React, TypeScript, Tailwind CSS, React Flow
