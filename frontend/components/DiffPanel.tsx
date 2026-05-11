"use client";

import { useState, useRef } from "react";
import { Upload, X, Plus, Pencil, Trash2, RefreshCw, ChevronDown, ChevronRight, GitBranch, CloudUpload, CloudDownload } from "lucide-react";
import type { DiffResult, DiffNode } from "@/lib/types";
import type { DevOpsConfig } from "./DevOpsSettings";
import { loadDesiredState, saveDesiredState } from "@/lib/api";

const STATUS_STYLES = {
  create: { label: "Create", badge: "bg-green-900 border-green-700 text-green-300", dot: "bg-green-500" },
  update: { label: "Update", badge: "bg-amber-900 border-amber-700 text-amber-300", dot: "bg-amber-500" },
  delete: { label: "Delete", badge: "bg-red-900 border-red-700 text-red-300", dot: "bg-red-500" },
  unchanged: { label: "OK", badge: "bg-slate-800 border-slate-600 text-slate-400", dot: "bg-slate-500" },
};

function starterIntentJson(subscriptionId: string) {
  return JSON.stringify({
    schemaVersion: "1.0",
    intent: "Create a demo web app backed by storage.",
    scope: {
      subscriptionId: subscriptionId || "<subscription-id>",
      resourceGroup: "rg-im-demo",
      location: "eastus",
    },
    components: [
      { kind: "storageAccount", name: "stimdemo001", replication: "LRS", publicAccess: false },
      { kind: "webApp", name: "app-im-demo", runtime: "dotnet", sku: "B1" },
    ],
    constraints: {
      tags: { owner: "cloudops", env: "demo" },
    },
  }, null, 2);
}

function DiffSection({
  title,
  nodes,
  status,
}: {
  title: string;
  nodes: DiffNode[];
  status: keyof typeof STATUS_STYLES;
}) {
  const [open, setOpen] = useState(true);
  const s = STATUS_STYLES[status];
  if (nodes.length === 0) return null;

  return (
    <div className="border border-slate-700 rounded-lg overflow-hidden">
      <button
        onClick={() => setOpen((o) => !o)}
        className="w-full flex items-center gap-2 px-3 py-2 bg-slate-800 hover:bg-slate-750 text-left"
      >
        {open ? <ChevronDown size={12} className="text-slate-400" /> : <ChevronRight size={12} className="text-slate-400" />}
        <span className={`px-1.5 py-0.5 rounded border text-[10px] font-semibold ${s.badge}`}>{s.label}</span>
        <span className="text-xs text-slate-300 font-medium">{title}</span>
        <span className="ml-auto text-[10px] text-slate-500">{nodes.length}</span>
      </button>
      {open && (
        <div className="divide-y divide-slate-800">
          {nodes.map((n, i) => (
            <div key={i} className="px-3 py-2">
              <div className="flex items-center gap-2">
                <div className={`w-1.5 h-1.5 rounded-full flex-shrink-0 ${s.dot}`} />
                <span className="text-xs text-slate-200 font-medium">{n.name}</span>
              </div>
              <div className="text-[10px] text-slate-500 mt-0.5 pl-3.5">
                {n.type.split("/").pop()} · {n.resourceGroup}
              </div>
              {n.changes.length > 0 && (
                <div className="mt-1 pl-3.5 space-y-0.5">
                  {n.changes.map((c, j) => (
                    <div key={j} className="text-[10px] text-slate-400">
                      <span className="text-slate-500">{c.field}:</span>{" "}
                      <span className="text-red-400 line-through">{c.from ?? "—"}</span>
                      {" → "}
                      <span className="text-green-400">{c.to ?? "—"}</span>
                    </div>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

interface Props {
  subscriptionId: string;
  onDiff: (desiredJson: string) => void;
  onApply: (result: DiffResult, desiredJson: string) => void;
  onClose: () => void;
  onOpenSettings: () => void;
  result: DiffResult | null;
  loading: boolean;
  error: string | null;
  applyLoading: boolean;
  applyStatus: string | null;
  applyError: string | null;
  devOpsConfig: DevOpsConfig;
}

export default function DiffPanel({
  subscriptionId,
  onDiff,
  onApply,
  onClose,
  onOpenSettings,
  result,
  loading,
  error,
  applyLoading,
  applyStatus,
  applyError,
  devOpsConfig
}: Props) {
  const fileRef = useRef<HTMLInputElement>(null);
  const [editorValue, setEditorValue] = useState<string>("");
  const [showEditor, setShowEditor] = useState(false);
  const [adoLoading, setAdoLoading] = useState(false);
  const [adoError, setAdoError] = useState<string | null>(null);
  const [adoStatus, setAdoStatus] = useState<string | null>(null);

  const hasAdoConfig = Boolean(devOpsConfig.orgUrl && devOpsConfig.project && devOpsConfig.repository && devOpsConfig.pat);

  async function handleLoadFromDevOps() {
    setAdoLoading(true); setAdoError(null); setAdoStatus(null);
    try {
      const spec = await loadDesiredState(devOpsConfig);
      const json = JSON.stringify(spec, null, 2);
      setEditorValue(json);
      setShowEditor(true);
      setAdoStatus("Loaded from Azure DevOps.");
    } catch (e) {
      const message = (e as Error).message;
      if (message.includes("File not found in repository")) {
        setEditorValue(starterIntentJson(subscriptionId));
        setShowEditor(true);
        setAdoStatus(`No file at ${devOpsConfig.filePath} on ${devOpsConfig.branch}. Starter loaded; Save to DevOps will create it.`);
      } else {
        setAdoError(message);
      }
    }
    finally { setAdoLoading(false); }
  }

  async function handleSaveToDevOps() {
    if (!editorValue.trim()) return;
    setAdoLoading(true); setAdoError(null); setAdoStatus(null);
    try {
      let parsed;
      try { parsed = JSON.parse(editorValue); }
      catch { setAdoError("Invalid JSON — fix syntax errors before saving."); setAdoLoading(false); return; }
      const pretty = JSON.stringify(parsed, null, 2);
      await saveDesiredState(devOpsConfig, pretty);
      setAdoStatus("Saved to Azure DevOps repo.");
    } catch (e) { setAdoError((e as Error).message); }
    finally { setAdoLoading(false); }
  }

  function handleFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    const reader = new FileReader();
    reader.onload = (ev) => {
      const text = ev.target?.result as string;
      setEditorValue(text);
      setShowEditor(true);
    };
    reader.readAsText(file);
    e.target.value = "";
  }

  const totalChanges = result ? result.toCreate.length + result.toUpdate.length + result.toDelete.length : 0;
  const canApply = result !== null && totalChanges > 0;

  return (
    <div className="flex h-full min-h-0 flex-col overflow-hidden bg-[#161b27] text-slate-200">
      <div className="sticky top-0 z-20 flex h-11 flex-shrink-0 items-center gap-2 border-b border-slate-700 bg-[#161b27] px-4">
        <RefreshCw size={13} className="text-blue-400 flex-shrink-0" />
        <span className="text-xs font-semibold text-slate-300 flex-1 truncate">Infrastructure Diff</span>
        <button
          onClick={onOpenSettings}
          title={hasAdoConfig ? "DevOps connected" : "Connect Azure DevOps"}
          className={`flex-shrink-0 flex items-center gap-1 text-[10px] px-2 py-0.5 rounded border transition-colors ${
            hasAdoConfig
              ? "border-blue-700 text-blue-400 hover:bg-blue-900/30"
              : "border-slate-600 text-slate-500 hover:text-slate-300"
          }`}
        >
          <GitBranch size={10} />
          {hasAdoConfig ? "DevOps" : "Connect"}
        </button>
        <button
          onClick={onClose}
          title="Close diff"
          className="flex h-7 w-7 flex-shrink-0 items-center justify-center rounded text-slate-500 hover:bg-slate-800 hover:text-slate-300"
        >
          <X size={14} />
        </button>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto px-3 py-3 space-y-3">
        {/* Input area */}
        <div className="space-y-2">
          <div className="flex flex-wrap gap-2">
            <button
              onClick={() => fileRef.current?.click()}
              className="flex items-center gap-1.5 text-xs bg-slate-700 hover:bg-slate-600 px-3 py-1.5 rounded transition-colors"
            >
              <Upload size={12} />
              Upload JSON
            </button>
            <button
              onClick={() => setShowEditor((s) => !s)}
              className="flex items-center gap-1.5 text-xs bg-slate-700 hover:bg-slate-600 px-3 py-1.5 rounded transition-colors"
            >
              <Pencil size={12} />
              {showEditor ? "Hide editor" : "Paste JSON"}
            </button>
            {hasAdoConfig && (
              <>
                <button
                  onClick={handleLoadFromDevOps}
                  disabled={adoLoading}
                  className="flex items-center gap-1.5 text-xs bg-blue-900/50 hover:bg-blue-900 border border-blue-700/50 text-blue-300 px-3 py-1.5 rounded transition-colors disabled:opacity-50"
                >
                  <CloudDownload size={12} />
                  Load from DevOps
                </button>
                <button
                  onClick={handleSaveToDevOps}
                  disabled={adoLoading || !editorValue.trim()}
                  className="flex items-center gap-1.5 text-xs bg-blue-900/50 hover:bg-blue-900 border border-blue-700/50 text-blue-300 px-3 py-1.5 rounded transition-colors disabled:opacity-50"
                >
                  <CloudUpload size={12} />
                  Save to DevOps
                </button>
              </>
            )}
            <input ref={fileRef} type="file" accept=".json" className="hidden" onChange={handleFile} />
          </div>
          {adoStatus && <div className="text-[10px] text-green-400">{adoStatus}</div>}
          {adoError && <div className="text-[10px] text-red-400">{adoError}</div>}

          {showEditor && (
            <textarea
              value={editorValue}
              onChange={(e) => setEditorValue(e.target.value)}
              placeholder={starterIntentJson(subscriptionId)}
              rows={8}
              className="w-full bg-[#0f1117] border border-slate-700 rounded px-3 py-2 text-[11px] text-slate-300 font-mono resize-none outline-none focus:border-blue-500"
            />
          )}

          {(editorValue || showEditor) && (
            <button
              onClick={() => onDiff(editorValue)}
              disabled={!editorValue.trim() || loading || !subscriptionId}
              className="w-full flex items-center justify-center gap-1.5 text-xs bg-blue-700 hover:bg-blue-600 disabled:bg-slate-700 disabled:text-slate-500 text-white px-3 py-1.5 rounded transition-colors"
            >
              {loading ? <RefreshCw size={12} className="animate-spin" /> : <RefreshCw size={12} />}
              {loading ? "Computing diff…" : "Run Diff"}
            </button>
          )}
        </div>

        {error && (
          <div className="text-xs text-red-400 bg-red-950 border border-red-800 rounded px-3 py-2">{error}</div>
        )}

        {result && (
          <>
            {/* Summary bar */}
            <div className="flex gap-2 text-[10px]">
              {result.toCreate.length > 0 && (
                <span className="flex items-center gap-1 text-green-400"><Plus size={10} />{result.toCreate.length} to create</span>
              )}
              {result.toUpdate.length > 0 && (
                <span className="flex items-center gap-1 text-amber-400"><Pencil size={10} />{result.toUpdate.length} to update</span>
              )}
              {result.toDelete.length > 0 && (
                <span className="flex items-center gap-1 text-red-400"><Trash2 size={10} />{result.toDelete.length} to delete</span>
              )}
              {totalChanges === 0 && (
                <span className="text-slate-400">No changes — infrastructure matches desired state.</span>
              )}
            </div>

            <DiffSection title="Resources to create" nodes={result.toCreate} status="create" />
            <DiffSection title="Resources to update" nodes={result.toUpdate} status="update" />
            <DiffSection title="Resources to delete" nodes={result.toDelete} status="delete" />
            <DiffSection title="Unchanged" nodes={result.unchanged} status="unchanged" />
          </>
        )}
      </div>

      {canApply && (
        <div className="px-3 py-3 border-t border-slate-700 flex-shrink-0">
          <button
            onClick={() => onApply(result!, editorValue)}
            disabled={applyLoading}
            className="w-full flex items-center justify-center gap-1.5 text-xs bg-green-700 hover:bg-green-600 disabled:bg-slate-700 disabled:text-slate-500 text-white px-3 py-2 rounded transition-colors font-medium"
          >
            {applyLoading ? <RefreshCw size={12} className="animate-spin" /> : null}
            {applyLoading ? "Starting agent..." : `Apply ${totalChanges} change${totalChanges !== 1 ? "s" : ""} via Agent`}
          </button>
          {applyStatus && <p className="text-[10px] text-green-400 mt-1.5 text-center">{applyStatus}</p>}
          {applyError && <p className="text-[10px] text-red-400 mt-1.5 text-center">{applyError}</p>}
          {!applyStatus && !applyError && (
            <p className="text-[10px] text-slate-600 mt-1.5 text-center">
              Starts the agent in your terminal; plans, questions, and status appear there
            </p>
          )}
        </div>
      )}
    </div>
  );
}
