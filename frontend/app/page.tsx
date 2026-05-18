"use client";

import { useState, useCallback, useEffect } from "react";
import dynamic from "next/dynamic";
import { RefreshCw, Network, AlertCircle, GitCompare, GitBranch } from "lucide-react";
import { fetchGraph } from "@/lib/api";
import type { ChatMessage, InfrastructureGraph, ResourceNode } from "@/lib/types";
import ResourcePanel from "@/components/ResourcePanel";
import DiffPanel from "@/components/DiffPanel";
import DevOpsSettings, { type DevOpsConfig } from "@/components/DevOpsSettings";
import ChatPanel from "@/components/ChatPanel";

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

export default function Home() {
  const [subscriptionId, setSubscriptionId] = useState(DEFAULT_SUB);
  const [sessionId, setSessionId] = useState("cloudops-ui-session");
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [tokenUsage, setTokenUsage] = useState({ input: 0, output: 0 });
  const [chatContextNodes, setChatContextNodes] = useState<ResourceNode[]>([]);
  const [syntheticPrompt, setSyntheticPrompt] = useState<string | null>(null);
  const [graph, setGraph] = useState<InfrastructureGraph | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [selectedNode, setSelectedNode] = useState<ResourceNode | null>(null);
  const [showDiff, setShowDiff] = useState(false);
  const [diffStatus] = useState<Record<string, "create" | "update" | "delete">>({});
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
    setChatContextNodes((prev) =>
      prev.some((item) => item.id === node.id) ? prev : [...prev, node]
    );
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
              onApplyToChat={(prompt) => setSyntheticPrompt(prompt)}
              onClose={() => setShowDiff(false)}
              onOpenSettings={() => setShowDevOpsSettings(true)}
              devOpsConfig={devOpsConfig}
            />
          </div>

          {/* Resource detail panel */}
          {selectedNode && !showDiff && (
            <ResourcePanel node={selectedNode} onClose={() => setSelectedNode(null)} />
          )}
        </div>

        <aside className="min-h-0 w-[420px] flex-shrink-0 border-l border-slate-700">
          <ChatPanel
            sessionId={sessionId}
            subscriptionId={subscriptionId}
            messages={messages}
            onMessagesChange={setMessages}
            onSessionIdSet={setSessionId}
            onDeploymentComplete={() => loadGraph(subscriptionId)}
            tokenUsage={tokenUsage}
            onTokenUsage={setTokenUsage}
            contextNodes={chatContextNodes}
            onRemoveContext={(id) => setChatContextNodes((prev) => prev.filter((node) => node.id !== id))}
            syntheticPrompt={syntheticPrompt}
            onSyntheticPromptConsumed={() => setSyntheticPrompt(null)}
            onResetChat={() => {
              setMessages([]);
              setTokenUsage({ input: 0, output: 0 });
              setChatContextNodes([]);
              setSyntheticPrompt(null);
              setSessionId(`cloudops-ui-session-${Date.now().toString(36)}`);
            }}
          />
        </aside>
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
