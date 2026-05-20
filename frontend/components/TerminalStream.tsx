"use client";

import { useEffect, useState } from "react";

import type { AgentActivityItem } from "@/lib/types";

const TOOL_PHRASES: Record<string, { running: string; done: string }> = {
  list_resource_groups: {
    running: "scanning resource groups...",
    done: "scanning resource groups done",
  },
  find_resource_group: {
    running: "finding resource group...",
    done: "finding resource group done",
  },
  list_resources: {
    running: "scanning resource inventory...",
    done: "scanning resource inventory done",
  },
  find_resource: {
    running: "finding resource...",
    done: "finding resource done",
  },
  get_resource_properties: {
    running: "reading resource properties...",
    done: "reading resource properties done",
  },
  get_resource_group_properties: {
    running: "reading resource group properties...",
    done: "reading resource group properties done",
  },
  analyze_resource_dependencies: {
    running: "analyzing resource dependencies...",
    done: "analyzing resource dependencies done",
  },
  analyze_resource_group_dependencies: {
    running: "analyzing resource group dependencies...",
    done: "analyzing resource group dependencies done",
  },
  infer_intent: {
    running: "inferring infrastructure intent...",
    done: "infrastructure intent inferred",
  },
  critic: {
    running: "checking plan shortfall...",
    done: "plan shortfall checked",
  },
  brainstorm: {
    running: "brainstorming ideas...",
    done: "brainstorming ideas done",
  },
  propose_plan: {
    running: "organizing chronological plan...",
    done: "chronological plan organized",
  },
  create_resource_group: {
    running: "creating resource group...",
    done: "resource group created",
  },
  deploy_resource: {
    running: "deploying resource...",
    done: "resource deployed",
  },
  update_resource: {
    running: "updating resource...",
    done: "resource updated",
  },
  rethink_deployment: {
    running: "rethinking deployment...",
    done: "deployment recovered",
  },
  delete_resource: {
    running: "deleting resource...",
    done: "resource deleted",
  },
  delete_resource_group: {
    running: "deleting resource group...",
    done: "resource group deleted",
  },
  verify_resource_exists: {
    running: "verifying resource...",
    done: "resource verified",
  },
  verify_resource_group_exists: {
    running: "verifying resource group...",
    done: "resource group verified",
  },
  trace_dependencies: {
    running: "establishing dependency edges...",
    done: "dependency graph built",
  },
  whatif_arm_template: {
    running: "simulating deployment impact...",
    done: "what-if analysis complete",
  },
  create_plan: { running: "cooking up a plan...", done: "plan drafted" },
  ask_clarifying_question: {
    running: "raising clarification...",
    done: "question raised",
  },
  create_or_update_resource: {
    running: "applying resource change...",
    done: "resource committed",
  },
  deploy_arm_template: {
    running: "deploying arm template...",
    done: "template deployed",
  },
  get_deployment_status: {
    running: "checking deployment status...",
    done: "deployment verified",
  },
};

const AGENT_PHRASES: Record<string, string> = {
  ReadAgent: "reading infrastructure",
  PlanAgent: "drafting operations plan",
  ExecuteAgent: "executing approved changes",
  "infra-reader-agent": "invoking infra-reader-agent",
  "infra-crawler-agent": "invoking infra-crawler-agent",
  "infra-planner-agent": "invoking infra-planner-agent",
  "infra-builder-agent": "invoking infra-builder-agent",
};

const GROUP_CHAT_PHRASES = [
  "agents cooking",
  "waking up the group chat",
  "rounding up the agents",
  "syncing the crew",
];
const GROUP_CHAT_DONE_PHRASE = "agents cooking";

const COMMIT_TOOLS = new Set([
  "create_or_update_resource",
  "deploy_arm_template",
  "create_resource_group",
  "deploy_resource",
  "update_resource",
  "rethink_deployment",
]);

const DELETE_TOOLS = new Set(["delete_resource", "delete_resource_group"]);

export interface TerminalLine {
  id: string;
  text: string;
  state: "done" | "running" | "failed" | "info";
}

function extractResourceLabel(item: AgentActivityItem): string {
  const msg = item.message ?? item.detailPreview ?? "";
  const match = msg.match(
    /([A-Za-z0-9._]+\/[A-Za-z0-9._/]+):?\s*([A-Za-z0-9._-]+)?/,
  );
  if (match) {
    const type = match[1];
    const name = match[2] ?? "";
    return name ? `${type}:${name}` : type;
  }
  return item.summary || "resource";
}

export function deriveLines(
  activities: AgentActivityItem[] | undefined,
  groupChatPhrase = GROUP_CHAT_PHRASES[0],
): TerminalLine[] {
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

    if (item.kind === "group_chat") {
      if (item.status === "running") {
        lines.push({
          id: item.id,
          text: `${groupChatPhrase}...`,
          state: "running",
        });
      } else if (item.status === "failed") {
        lines.push({
          id: item.id,
          text: `${GROUP_CHAT_DONE_PHRASE} failed: ${item.message ?? item.summary}`,
          state: "failed",
        });
      } else {
        lines.push({
          id: item.id,
          text: `${GROUP_CHAT_DONE_PHRASE} done`,
          state: "done",
        });
      }
      continue;
    }

    if (item.kind === "agent") {
      const agentPhrase = item.agent
        ? (AGENT_PHRASES[item.agent] ?? item.agent)
        : "Agent working";
      if (item.status === "running") {
        lines.push({
          id: item.id,
          text: `${agentPhrase}...`,
          state: "running",
        });
      } else if (item.status === "failed") {
        lines.push({
          id: item.id,
          text: `${agentPhrase} failed: ${item.message ?? item.summary}`,
          state: "failed",
        });
      } else {
        lines.push({ id: item.id, text: `${agentPhrase} done`, state: "done" });
      }
      continue;
    }

    if (item.kind === "tool" && item.tool) {
      const phrases = TOOL_PHRASES[item.tool];
      const runningText = phrases?.running ?? `${item.tool}...`;
      const doneText = phrases?.done ?? item.tool;

      if (item.status === "running") {
        lines.push({ id: item.id, text: runningText, state: "running" });
        continue;
      }
      if (item.status === "failed") {
        lines.push({
          id: item.id,
          text: `${doneText} failed${item.message ? `: ${item.message}` : ""}`,
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
      lines.push({ id: item.id, text: doneText, state: baseState });
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
  const hasRunningGroupChat = activities?.some(
    (item) => item.kind === "group_chat" && item.status === "running",
  );
  const [phraseIndex, setPhraseIndex] = useState(0);

  useEffect(() => {
    if (!hasRunningGroupChat) {
      return;
    }

    const timer = window.setInterval(() => {
      setPhraseIndex((current) => (current + 1) % GROUP_CHAT_PHRASES.length);
    }, 3200);

    return () => window.clearInterval(timer);
  }, [hasRunningGroupChat]);

  const lines = deriveLines(
    activities,
    GROUP_CHAT_PHRASES[phraseIndex] ?? GROUP_CHAT_PHRASES[0],
  );

  if (lines.length === 0) {
    return emptyHint ? (
      <div className="font-mono text-[11px] text-slate-600 italic">
        {emptyHint}
      </div>
    ) : null;
  }

  const seen = new Set<string>();
  const deduped = lines.filter((l) =>
    seen.has(l.id) ? false : (seen.add(l.id), true),
  );

  return (
    <ul className="space-y-1 font-mono text-[11px] leading-relaxed">
      {deduped.map((line, idx) => (
        <li key={`${line.id}-${idx}`} className="flex items-start gap-2">
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
