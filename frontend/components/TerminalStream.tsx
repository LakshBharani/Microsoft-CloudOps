"use client";

import type { AgentActivityItem } from "@/lib/types";

const TOOL_PHRASES: Record<string, string> = {
  decompose_intent: "Decomposing intent into blocks",
  list_resource_groups: "Reading resource groups",
  list_resources: "Inspecting resources",
  ask_clarifying_question: "Asking clarification",
  get_resource: "Reading resource",
  find_resource: "Finding resource",
  get_deployment_status: "Checking deployment status",
  create_plan: "Drafting plan",
  create_or_update_resource: "Applying resource change",
  delete_resource: "Deleting resource",
  deploy_arm_template: "Deploying ARM template",
};

const COMMIT_TOOLS = new Set([
  "create_or_update_resource",
  "deploy_arm_template",
]);

const DELETE_TOOLS = new Set(["delete_resource"]);

export interface TerminalLine {
  id: string;
  text: string;
  state: "done" | "running" | "failed" | "info";
}

function extractResourceLabel(item: AgentActivityItem): string {
  const msg = item.message ?? item.detailPreview ?? "";
  const match = msg.match(/([A-Za-z0-9._]+\/[A-Za-z0-9._/]+):?\s*([A-Za-z0-9._-]+)?/);
  if (match) {
    const type = match[1];
    const name = match[2] ?? "";
    return name ? `${type}:${name}` : type;
  }
  return item.summary || "resource";
}

export function deriveLines(activities: AgentActivityItem[] | undefined): TerminalLine[] {
  if (!activities || activities.length === 0) return [];
  const lines: TerminalLine[] = [];
  let commitCount = 0;

  for (const item of activities) {
    const baseState: TerminalLine["state"] =
      item.status === "running"
        ? "running"
        : item.status === "failed"
          ? "failed"
          : "done";

    if (item.kind === "agent") {
      if (item.status === "running") {
        lines.push({
          id: item.id,
          text: `${item.summary || "InfraMapper agent working"}...`,
          state: "running",
        });
      } else if (item.status === "failed") {
        lines.push({
          id: item.id,
          text: `Agent failed: ${item.message ?? item.summary}`,
          state: "failed",
        });
      }
      continue;
    }

    if (item.kind === "tool" && item.tool) {
      const phrase = TOOL_PHRASES[item.tool] ?? item.tool;
      if (item.status === "running") {
        lines.push({
          id: item.id,
          text: `${phrase}...`,
          state: "running",
        });
        continue;
      }
      if (item.status === "failed") {
        lines.push({
          id: item.id,
          text: `${phrase} failed${item.message ? `: ${item.message}` : ""}`,
          state: "failed",
        });
        continue;
      }
      if (COMMIT_TOOLS.has(item.tool) && item.status === "success") {
        commitCount += 1;
        lines.push({
          id: item.id,
          text: `Committed [${commitCount}] ${extractResourceLabel(item)}`,
          state: "done",
        });
        continue;
      }
      if (DELETE_TOOLS.has(item.tool) && item.status === "success") {
        commitCount += 1;
        lines.push({
          id: item.id,
          text: `Deleted [${commitCount}] ${extractResourceLabel(item)}`,
          state: "done",
        });
        continue;
      }
      lines.push({
        id: item.id,
        text: `${phrase} done`,
        state: baseState,
      });
      continue;
    }

    if (item.kind === "error") {
      lines.push({
        id: item.id,
        text: item.message ?? item.summary ?? "Error",
        state: "failed",
      });
      continue;
    }

    lines.push({
      id: item.id,
      text: item.summary || item.message || "step",
      state: baseState,
    });
  }

  return lines;
}

export default function TerminalStream({
  activities,
  emptyHint,
}: {
  activities?: AgentActivityItem[];
  emptyHint?: string;
}) {
  const lines = deriveLines(activities);

  if (lines.length === 0) {
    return emptyHint ? (
      <div className="font-mono text-[11px] text-slate-600 italic">{emptyHint}</div>
    ) : null;
  }

  return (
    <ul className="space-y-1 font-mono text-[11px] leading-relaxed">
      {lines.map((line) => (
        <li key={line.id} className="flex items-start gap-2">
          <span
            className={
              line.state === "running"
                ? "mt-1.5 inline-block h-1.5 w-1.5 flex-shrink-0 rounded-full bg-cyan-400 animate-pulse shadow-[0_0_8px_rgba(34,211,238,0.6)]"
                : line.state === "failed"
                  ? "mt-1.5 inline-block h-1.5 w-1.5 flex-shrink-0 rounded-full bg-red-400"
                  : "mt-1.5 inline-block h-1.5 w-1.5 flex-shrink-0 rounded-full bg-slate-500"
            }
          />
          <span
            className={
              line.state === "failed"
                ? "text-red-300 break-words"
                : line.state === "running"
                  ? "text-slate-200 break-words"
                  : "text-slate-400 break-words"
            }
          >
            {line.text}
          </span>
        </li>
      ))}
    </ul>
  );
}
