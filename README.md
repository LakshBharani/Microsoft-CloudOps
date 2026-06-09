# InfraMapper

AI-powered Azure infrastructure management. Chat with a multi-agent system to plan, inspect, and deploy Azure resources — with a live infra graph, diff viewer, and Azure DevOps integration.

## Demo

<!-- Upload your video here -->

---

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                    Next.js Frontend                 │
│  ChatPanel · InfraGraph · DiffPanel · PlanCard      │
└────────────────────┬────────────────────────────────┘
                     │ SSE + REST
┌────────────────────▼────────────────────────────────┐
│                  FastAPI Backend                     │
│                                                     │
│         CloudOps Group Chat (orchestrator)          │
│  ┌──────────────┐  ┌──────────────┐                 │
│  │ InfraPlanner │  │  InfraReader │                 │
│  │  (plan/critic│  │ (live state) │                 │
│  │   /propose)  │  │              │                 │
│  └──────────────┘  └──────────────┘                 │
│  ┌──────────────┐  ┌──────────────┐                 │
│  │ InfraCrawler │  │ InfraBuilder │                 │
│  │  (analyze)   │  │ (deploy/CRUD)│                 │
│  └──────────────┘  └──────────────┘                 │
└─────────────────────────────────────────────────────┘
                     │
              Azure AI Foundry (GPT-4.1-mini)
              Azure Resource Manager
              Azure DevOps
```

**Four specialized agents** collaborate via Semantic Kernel group chat:

| Agent | Role |
|-------|------|
| **InfraPlanner** | Infers intent, proposes plans, runs critic loop |
| **InfraReader** | Reads live Azure resource state |
| **InfraCrawler** | Analyzes resources, dependencies, topology |
| **InfraBuilder** | Creates, updates, and deletes Azure resources |

## Features

- **Natural language infrastructure management** — describe what you want, agents handle the rest
- **Live infra graph** — visual map of your Azure subscription using React Flow
- **Plan & approve flow** — agents propose a plan before touching anything; you approve or reject
- **Diff viewer** — compare desired state vs live state
- **Azure DevOps integration** — read/write desired state JSON directly to a repo
- **Streaming responses** — real-time activity feed via SSE

## Tech Stack

| Layer | Stack |
|-------|-------|
| Frontend | Next.js 16, React 19, TypeScript, Tailwind CSS |
| Infra visualization | React Flow (`@xyflow/react`) |
| Backend | Python 3.12, FastAPI, Uvicorn |
| AI agents | Semantic Kernel 1.42, Azure AI Agents SDK |
| Azure | Azure AI Foundry, Azure Resource Manager, Azure DevOps |

## Prerequisites

- Python 3.12+
- Node.js 20+
- Azure subscription
- Azure AI Foundry project (for agent model)
- `az login` completed (uses DefaultAzureCredential)

## Setup

### Backend

```bash
cd backend
cp .env.example .env
# Fill in your values (see Environment Variables below)

uv sync          # or: pip install -r requirements.txt
uvicorn app.main:app --reload
```

### Frontend

```bash
cd frontend
npm install
cp .env.local.example .env.local   # if present, or set NEXT_PUBLIC_API_URL
npm run dev
```

Open [http://localhost:3000](http://localhost:3000).

## Environment Variables

### Backend (`backend/.env`)

| Variable | Required | Description |
|----------|----------|-------------|
| `AZURE_AI_AGENT_PROJECT_CONNECTION_STRING` | Yes | Azure AI Foundry project connection string |
| `AZURE_AI_AGENT_MODEL_DEPLOYMENT_NAME` | Yes | Model deployment name (e.g. `gpt-4.1-mini`) |
| `AZURE_SUBSCRIPTION_ID` | No | Default subscription ID (can also be passed per-request) |
| `SERPAPI_KEY` | No | Enables web search in agent tools |

## Usage

1. **Chat** — type a request like *"Add a storage account in East US"* or *"What resources do I have in my subscription?"*
2. **Review the plan** — the InfraPlanner proposes operations; inspect and approve or reject
3. **Watch the graph** — InfraGraph updates as resources change
4. **Diff desired state** — connect to Azure DevOps to track desired vs live state

## Project Structure

```
├── backend/
│   └── app/
│       ├── agents/          # AzureAIAgent definitions (planner/reader/crawler/builder)
│       ├── plugins/         # Semantic Kernel plugins (tool implementations)
│       ├── group_chats/     # Multi-agent orchestration
│       ├── azure_resources.py
│       ├── devops.py        # Azure DevOps read/write
│       ├── diff.py          # Desired vs live state diff
│       └── main.py          # FastAPI app + SSE streaming
├── frontend/
│   ├── app/                 # Next.js app router
│   └── components/
│       ├── ChatPanel.tsx
│       ├── InfraGraph.tsx
│       ├── DiffPanel.tsx
│       ├── PlanCard.tsx
│       └── ...
└── infra/
    └── desired-state.example.json
```

## API Reference

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/infra` | Live Azure resource nodes |
| `POST` | `/api/agent/stream` | Stream agent chat (SSE) |
| `POST` | `/api/agent/plan/{id}/approve` | Approve a plan |
| `POST` | `/api/agent/plan/{id}/reject` | Reject a plan |
| `POST` | `/api/diff` | Diff desired state vs live |
| `GET` | `/api/desiredstate` | Fetch desired-state from Azure DevOps |
| `PUT` | `/api/desiredstate` | Push desired-state to Azure DevOps |
