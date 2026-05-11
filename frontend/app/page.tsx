"use client";

import { useState, useCallback, useEffect } from "react";
import dynamic from "next/dynamic";
import { RefreshCw, Network, AlertCircle, GitCompare, GitBranch } from "lucide-react";
import { fetchGraph, diffInfra, startTerminalChat } from "@/lib/api";
import type { InfrastructureGraph, ResourceNode, DiffResult } from "@/lib/types";
import ResourcePanel from "@/components/ResourcePanel";
import DiffPanel from "@/components/DiffPanel";
import DevOpsSettings, { type DevOpsConfig } from "@/components/DevOpsSettings";

const InfraGraph = dynamic(() => import("@/components/InfraGraph"), { ssr: false });

const DEFAULT_SUB = process.env.NEXT_PUBLIC_SUBSCRIPTION_ID ?? "";
const DEFAULT_DEVOPS_CONFIG: DevOpsConfig = {
  orgUrl: "",
  project: "",
  repository: "",
  pat: "",
  branch: "main",
  filePath: "infra/desired-state.json"
};

function summarizeIntentComponents(desiredJson?: string): string[] {
  if (!desiredJson?.trim()) return [];

  try {
    const spec = JSON.parse(desiredJson) as {
      scope?: { resourceGroup?: string };
      components?: Array<{ kind?: string; name?: string; subnets?: Array<{ name?: string }> }>;
    };

    const lines: string[] = [];
    if (spec.scope?.resourceGroup) {
      lines.push(`- Microsoft.Resources/resourceGroups "${spec.scope.resourceGroup}"`);
    }

    for (const component of spec.components ?? []) {
      if (!component?.kind || !component?.name) continue;
      lines.push(`- ${component.kind} "${component.name}"`);
      if (component.kind.toLowerCase() === "virtualnetwork") {
        for (const subnet of component.subnets ?? []) {
          if (subnet?.name) lines.push(`- subnet "${subnet.name}"`);
        }
      }
    }

    return lines;
  } catch {
    return [];
  }
}

export default function Home() {
  const [subscriptionId, setSubscriptionId] = useState(DEFAULT_SUB);
  const [graph, setGraph] = useState<InfrastructureGraph | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedNode, setSelectedNode] = useState<ResourceNode | null>(null);
  const [showDiff, setShowDiff] = useState(false);
  const [diffResult, setDiffResult] = useState<DiffResult | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);
  const [applyLoading, setApplyLoading] = useState(false);
  const [applyStatus, setApplyStatus] = useState<string | null>(null);
  const [applyError, setApplyError] = useState<string | null>(null);
  const [diffStatus, setDiffStatus] = useState<Record<string, "create" | "update" | "delete">>({});
  const [devOpsConfig, setDevOpsConfig] = useState<DevOpsConfig>(DEFAULT_DEVOPS_CONFIG);

  useEffect(() => {
    const timeout = window.setTimeout(() => {
      try {
        const stored = localStorage.getItem("devops_config");
        if (stored) setDevOpsConfig(JSON.parse(stored));
      } catch {}
    }, 0);
    return () => window.clearTimeout(timeout);
  }, []);

  function handleDevOpsConfigChange(cfg: DevOpsConfig) {
    setDevOpsConfig(cfg);
    try { localStorage.setItem("devops_config", JSON.stringify(cfg)); } catch {}
  }
  const [showDevOpsSettings, setShowDevOpsSettings] = useState(false);

  function handleNodeClick(node: ResourceNode) {
    setSelectedNode(node);
  }

  async function handleRunDiff(desiredJson: string) {
    setDiffLoading(true);
    setDiffError(null);
    try {
      const spec = JSON.parse(desiredJson);
      const result = await diffInfra(subscriptionId, spec);
      setDiffResult(result);

      // Build diffStatus map keyed by existing ARM id for live nodes
      const status: Record<string, "create" | "update" | "delete"> = {};
      result.toUpdate.forEach((n) => { if (n.existingId) status[n.existingId] = "update"; });
      result.toDelete.forEach((n) => { if (n.existingId) status[n.existingId] = "delete"; });
      setDiffStatus(status);
    } catch (e) {
      setDiffError((e as Error).message);
    } finally {
      setDiffLoading(false);
    }
  }

  async function handleApplyDiff(result: DiffResult, desiredJson?: string) {
    // Send the original intent/desired JSON too. The diff is useful context, but intent JSON may
    // contain components the current deterministic diff/compiler cannot fully expand yet.
    const lines: string[] = [
      "Apply the following infrastructure intent. The JSON below is the authoritative source of truth for scope, components, tags, and constraints. Every component listed in the JSON MUST appear in the plan and ARM template, even if the diff does not mention it.",
      "The diff is REFERENCE ONLY — it shows current Azure state vs intent, but it does not define scope. Do not shrink the plan to match the diff."
    ];
    if (desiredJson?.trim()) {
      lines.push("");
      lines.push("Original infrastructure JSON (authoritative):");
      lines.push("```json");
      lines.push(desiredJson.trim());
      lines.push("```");
      lines.push("");
    }
    lines.push("Computed diff (reference only — current Azure state vs intent):");
    result.toCreate.forEach((n) => lines.push(`- Create ${n.type} "${n.name}" in resource group "${n.resourceGroup}" (${n.location})`));
    result.toUpdate.forEach((n) => {
      const changeDesc = n.changes.map((c) => `${c.field}: ${c.from} → ${c.to}`).join(", ");
      lines.push(`- Update ${n.type} "${n.name}": ${changeDesc}`);
    });
    result.toDelete.forEach((n) => lines.push(`- Delete ${n.type} "${n.name}" (id: ${n.existingId})`));
    const intentComponents = summarizeIntentComponents(desiredJson);
    if (intentComponents.length > 0) {
      lines.push("");
      lines.push("FINAL SCOPE CHECK — authoritative JSON wins over the computed diff.");
      lines.push("The plan and ARM template MUST include every item below. If a listed item is absent from the diff, still include it:");
      lines.push(...intentComponents);
      lines.push("Do not end with a plan that only contains the computed diff resources.");
    }
    const prompt = lines.join("\n");

    setApplyLoading(true);
    setApplyError(null);
    setApplyStatus(null);
    try {
      await startTerminalChat(prompt, subscriptionId);
      setApplyStatus("Agent started. Go to the terminal to view status.");
    } catch (e) {
      setApplyError((e as Error).message);
    } finally {
      setApplyLoading(false);
    }
  }

  const loadGraph = useCallback(async (subId: string) => {
    if (!subId.trim()) return;
    setLoading(true);
    setError(null);
    try {
      const g = await fetchGraph(subId.trim());
      setGraph(g);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoading(false);
    }
  }, []);

  function handleKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === "Enter") loadGraph(subscriptionId);
  }

  return (
    <div className="h-screen flex flex-col bg-[#0f1117] text-slate-200 overflow-hidden">
      {/* Topbar */}
      <header className="flex items-center gap-4 px-4 py-2 bg-[#161b27] border-b border-slate-700 flex-shrink-0">
        <div className="flex items-center gap-2">
          <Network size={18} className="text-blue-400" />
          <span className="font-semibold text-sm tracking-tight">InfraMapper</span>
        </div>
        <div className="flex items-center gap-2 flex-1 max-w-lg">
          <input
            value={subscriptionId}
            onChange={(e) => setSubscriptionId(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Azure subscription ID"
            className="flex-1 bg-[#1e293b] border border-slate-600 rounded px-3 py-1 text-xs text-slate-200 placeholder-slate-500 outline-none focus:border-blue-500"
          />
          <button
            onClick={() => loadGraph(subscriptionId)}
            disabled={loading || !subscriptionId.trim()}
            className="flex items-center gap-1.5 bg-blue-600 hover:bg-blue-500 disabled:bg-slate-700 disabled:text-slate-500 text-white text-xs px-3 py-1 rounded transition-colors"
          >
            <RefreshCw size={12} className={loading ? "animate-spin" : ""} />
            {loading ? "Loading…" : "Load"}
          </button>
          <button
            onClick={() => setShowDiff((s) => !s)}
            className={`flex items-center gap-1.5 text-xs px-3 py-1 rounded transition-colors border ${
              showDiff
                ? "bg-blue-700 border-blue-500 text-white"
                : "bg-transparent border-slate-600 text-slate-400 hover:text-slate-200 hover:border-slate-500"
            }`}
          >
            <GitCompare size={12} />
            Diff
          </button>
          <button
            onClick={() => setShowDevOpsSettings(true)}
            className="flex items-center gap-1.5 text-xs px-3 py-1 rounded transition-colors border border-slate-600 text-slate-400 hover:text-slate-200 hover:border-slate-500"
          >
            <GitBranch size={12} />
            DevOps
          </button>
        </div>
        {graph && (
          <div className="text-xs text-slate-500">
            {graph.nodes.length} nodes · {graph.edges.length} edges
          </div>
        )}
      </header>

      {/* Main area */}
      <div className="flex-1 min-h-0 flex overflow-hidden">
        {/* Left workspace */}
        <div className="min-h-0 min-w-0 flex-1 flex overflow-hidden">
          {/* Graph */}
          <div className="flex-1 min-w-0 relative">
            {error && (
              <div className="absolute inset-x-4 top-4 z-20 flex items-center gap-2 bg-red-950 border border-red-800 text-red-300 text-xs px-3 py-2 rounded">
                <AlertCircle size={14} />
                {error}
              </div>
            )}
            {!graph && !loading && (
              <div className="absolute inset-0 flex items-center justify-center text-slate-500 text-sm">
                Enter a subscription ID and click Load
              </div>
            )}
            {graph && (
              <InfraGraph graph={graph} onNodeClick={handleNodeClick} diffStatus={diffStatus} />
            )}
          </div>

          {/* Diff panel */}
          <div className={`${showDiff ? "w-80" : "hidden w-0"} min-h-0 flex-shrink-0 flex flex-col overflow-hidden border-l border-slate-700`}>
            <DiffPanel
              subscriptionId={subscriptionId}
              onDiff={handleRunDiff}
              onApply={handleApplyDiff}
              onClose={() => setShowDiff(false)}
              onOpenSettings={() => setShowDevOpsSettings(true)}
              result={diffResult}
              loading={diffLoading}
              error={diffError}
              applyLoading={applyLoading}
              applyStatus={applyStatus}
              applyError={applyError}
              devOpsConfig={devOpsConfig}
            />
          </div>

          {/* Resource detail panel */}
          {selectedNode && !showDiff && (
            <ResourcePanel node={selectedNode} onClose={() => setSelectedNode(null)} />
          )}
        </div>
      </div>

      {showDevOpsSettings && (
        <DevOpsSettings
          config={devOpsConfig}
          onChange={handleDevOpsConfigChange}
          onClose={() => setShowDevOpsSettings(false)}
        />
      )}
    </div>
  );
}
