"use client";

import { useState, useCallback, useEffect } from "react";
import dynamic from "next/dynamic";
import { RefreshCw, Network, AlertCircle, GitCompare } from "lucide-react";
import { fetchGraph, diffInfra } from "@/lib/api";
import type { InfrastructureGraph, ResourceNode, ChatMessage, Session, DiffResult } from "@/lib/types";
import ResourcePanel from "@/components/ResourcePanel";
import ChatPanel from "@/components/ChatPanel";
import SessionTabs from "@/components/SessionTabs";
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

function makeSession(n: number): Session {
  return { id: crypto.randomUUID(), name: `Session ${n}`, createdAt: Date.now() };
}

export default function Home() {
  const [subscriptionId, setSubscriptionId] = useState(DEFAULT_SUB);
  const [graph, setGraph] = useState<InfrastructureGraph | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedNode, setSelectedNode] = useState<ResourceNode | null>(null);
  const [contextNodes, setContextNodes] = useState<ResourceNode[]>([]);
  const [showDiff, setShowDiff] = useState(false);
  const [diffResult, setDiffResult] = useState<DiffResult | null>(null);
  const [diffLoading, setDiffLoading] = useState(false);
  const [diffError, setDiffError] = useState<string | null>(null);
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
    setContextNodes((prev) =>
      prev.find((n) => n.id === node.id) ? prev : [...prev, node]
    );
  }

  function handleRemoveContext(id: string) {
    setContextNodes((prev) => prev.filter((n) => n.id !== id));
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

  function handleApplyDiff(result: DiffResult) {
    // Build a natural-language prompt describing the diff and send it to the agent
    const lines: string[] = ["Apply the following infrastructure changes:"];
    result.toCreate.forEach((n) => lines.push(`- Create ${n.type} "${n.name}" in resource group "${n.resourceGroup}" (${n.location})`));
    result.toUpdate.forEach((n) => {
      const changeDesc = n.changes.map((c) => `${c.field}: ${c.from} → ${c.to}`).join(", ");
      lines.push(`- Update ${n.type} "${n.name}": ${changeDesc}`);
    });
    result.toDelete.forEach((n) => lines.push(`- Delete ${n.type} "${n.name}" (id: ${n.existingId})`));
    const prompt = lines.join("\n");

    setShowDiff(false);
    setSyntheticPrompt(prompt);
  }

  const [syntheticPrompt, setSyntheticPrompt] = useState<string | null>(null);

  const [sessions, setSessions] = useState<Session[]>(() => [makeSession(1)]);
  const [activeSessionId, setActiveSessionId] = useState<string>(() => sessions[0].id);
  const [sessionMessages, setSessionMessages] = useState<Map<string, ChatMessage[]>>(
    () => new Map([[sessions[0].id, []]])
  );
  const [sessionTokenUsage, setSessionTokenUsage] = useState<Map<string, { input: number; output: number }>>(
    () => new Map([[sessions[0].id, { input: 0, output: 0 }]])
  );
  const [sessionAgentIds, setSessionAgentIds] = useState<Map<string, string>>(
    () => new Map([[sessions[0].id, sessions[0].id]])
  );

  const activeMessages = sessionMessages.get(activeSessionId) ?? [];
  const activeTokenUsage = sessionTokenUsage.get(activeSessionId) ?? { input: 0, output: 0 };
  const activeAgentSessionId = sessionAgentIds.get(activeSessionId) ?? activeSessionId;

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

  function handleNewSession() {
    const n = sessions.length + 1;
    const s = makeSession(n);
    setSessions((prev) => [...prev, s]);
    setSessionMessages((prev) => new Map(prev).set(s.id, []));
    setSessionTokenUsage((prev) => new Map(prev).set(s.id, { input: 0, output: 0 }));
    setSessionAgentIds((prev) => new Map(prev).set(s.id, s.id));
    setActiveSessionId(s.id);
  }

  function handleDeleteSession(id: string) {
    setSessions((prev) => {
      const next = prev.filter((s) => s.id !== id);
      if (activeSessionId === id && next.length > 0) setActiveSessionId(next[next.length - 1].id);
      return next;
    });
  }

  function handleRenameSession(id: string, name: string) {
    setSessions((prev) => prev.map((s) => s.id === id ? { ...s, name } : s));
  }

  function autoNameFromFirstMessage(text: string) {
    const name = text.trim().slice(0, 30) + (text.trim().length > 30 ? "…" : "");
    setSessions((prev) => prev.map((s) => s.id === activeSessionId ? { ...s, name } : s));
  }

  function handleMessagesChange(msgs: ChatMessage[] | ((prev: ChatMessage[]) => ChatMessage[])) {
    setSessionMessages((prev) => {
      const current = prev.get(activeSessionId) ?? [];
      const next = typeof msgs === "function" ? msgs(current) : msgs;
      // Auto-name on first user message
      const currentSession = sessions.find((s) => s.id === activeSessionId);
      const firstUserMsg = next.find((m) => m.role === "user");
      if (firstUserMsg && currentSession && /^Session \d+$/.test(currentSession.name)) {
        autoNameFromFirstMessage(firstUserMsg.content);
      }
      return new Map(prev).set(activeSessionId, next);
    });
  }

  function handleTokenUsage(u: { input: number; output: number }) {
    setSessionTokenUsage((prev) => new Map(prev).set(activeSessionId, u));
  }

  function handleSessionIdSet(id: string) {
    setSessionAgentIds((prev) => new Map(prev).set(activeSessionId, id));
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
              devOpsConfig={devOpsConfig}
            />
          </div>

          {/* Resource detail panel */}
          {selectedNode && !showDiff && (
            <ResourcePanel node={selectedNode} onClose={() => setSelectedNode(null)} />
          )}
        </div>

        {/* Chat panel */}
        <div className="min-w-[620px] w-[44vw] max-w-[860px] min-h-0 flex-shrink-0 flex flex-col border-l border-slate-700">
          <SessionTabs
            sessions={sessions}
            activeId={activeSessionId}
            onSelect={setActiveSessionId}
            onNew={handleNewSession}
            onDelete={handleDeleteSession}
            onRename={handleRenameSession}
          />
          <div className="min-h-0 flex-1">
            <ChatPanel
              key={activeSessionId}
              sessionId={activeAgentSessionId}
              subscriptionId={subscriptionId}
              messages={activeMessages}
              onMessagesChange={handleMessagesChange}
              onSessionIdSet={handleSessionIdSet}
              onDeploymentComplete={() => loadGraph(subscriptionId)}
              tokenUsage={activeTokenUsage}
              onTokenUsage={handleTokenUsage}
              contextNodes={contextNodes}
              onRemoveContext={handleRemoveContext}
              syntheticPrompt={syntheticPrompt}
              onSyntheticPromptConsumed={() => setSyntheticPrompt(null)}
            />
          </div>
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
