import type { InfrastructureGraph, AgentStreamEvent } from "./types";

const BASE = process.env.NEXT_PUBLIC_API_URL ?? "http://localhost:5166";

export async function fetchGraph(subscriptionId: string): Promise<InfrastructureGraph> {
  const res = await fetch(`${BASE}/api/infra?subscriptionId=${subscriptionId}`);
  if (!res.ok) throw new Error(`Failed to fetch graph: ${res.statusText}`);
  return res.json();
}

export async function* streamChat(
  message: string,
  subscriptionId: string,
  sessionId?: string
): AsyncGenerator<AgentStreamEvent> {
  const res = await fetch(`${BASE}/api/agent/stream`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ message, subscriptionId, sessionId }),
  });

  if (!res.ok) throw new Error(`Agent error: ${res.statusText}`);

  const reader = res.body!.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    const lines = buffer.split("\n");
    buffer = lines.pop() ?? "";

    for (const line of lines) {
      if (line.startsWith("data: ")) {
        const json = line.slice(6).trim();
        if (json) yield JSON.parse(json) as AgentStreamEvent;
      }
    }
  }
}

export async function startTerminalChat(message: string, subscriptionId: string, sessionId?: string): Promise<void> {
  const res = await fetch(`${BASE}/api/agent/terminal`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ message, subscriptionId, sessionId }),
  });

  if (!res.ok) throw await errorFromResponse("Agent start failed", res);
}

async function errorFromResponse(prefix: string, res: Response): Promise<Error> {
  const body = await res.text().catch(() => "");
  return new Error(body ? `${prefix}: ${body}` : `${prefix}: ${res.statusText}`);
}

export async function loadDesiredState(cfg: import("../components/DevOpsSettings").DevOpsConfig): Promise<unknown> {
  const params = new URLSearchParams({
    orgUrl: cfg.orgUrl, project: cfg.project, repository: cfg.repository,
    pat: cfg.pat, branch: cfg.branch, filePath: cfg.filePath,
  });
  const res = await fetch(`${BASE}/api/desiredstate?${params}`);
  if (!res.ok) throw await errorFromResponse("Load failed", res);
  return res.json();
}

export async function saveDesiredState(cfg: import("../components/DevOpsSettings").DevOpsConfig, rawJson: string, commitMessage?: string): Promise<void> {
  const res = await fetch(`${BASE}/api/desiredstate`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ ...cfg, rawJson, commitMessage }),
  });
  if (!res.ok) throw await errorFromResponse("Save failed", res);
}

export async function diffInfra(subscriptionId: string, desired: import("./types").DesiredStateSpec): Promise<import("./types").DiffResult> {
  const res = await fetch(`${BASE}/api/diff?subscriptionId=${subscriptionId}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(desired),
  });
  if (!res.ok) throw await errorFromResponse("Diff failed", res);
  return res.json();
}

export async function approvePlan(planId: string, sessionId: string): Promise<void> {
  const res = await fetch(`${BASE}/api/agent/plan/${planId}/approve?sessionId=${sessionId}`, {
    method: "POST",
  });
  if (!res.ok) throw new Error(`Approve failed: ${res.statusText}`);
}

export async function rejectPlan(planId: string): Promise<void> {
  const res = await fetch(`${BASE}/api/agent/plan/${planId}/reject`, { method: "POST" });
  if (!res.ok) throw new Error(`Reject failed: ${res.statusText}`);
}

export async function answerQuestion(questionId: string, sessionId: string, answer: string): Promise<void> {
  const res = await fetch(`${BASE}/api/agent/question/${questionId}/answer?sessionId=${sessionId}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ answer }),
  });
  if (!res.ok) throw new Error(`Question answer failed: ${res.statusText}`);
}
