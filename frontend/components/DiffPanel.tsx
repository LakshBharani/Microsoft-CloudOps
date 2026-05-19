"use client";

import { useState, useRef } from "react";
import {
  Upload,
  X,
  Pencil,
  GitBranch,
  CloudUpload,
  CloudDownload,
  RefreshCw,
} from "lucide-react";
import type { DevOpsConfig } from "./DevOpsSettings";
import { loadDesiredState, saveDesiredState } from "@/lib/api";

function starterIntentJson(subscriptionId: string) {
  return JSON.stringify(
    {
      schemaVersion: "1.0",
      intent:
        "Deploy a lightweight Azure demo environment with only free or low-cost networking and storage resources. No compute resources.",
      scope: {
        subscriptionId: subscriptionId || "<subscription-id>",
        resourceGroup: "rg-im-lite",
        location: "eastus",
      },
      components: [
        {
          kind: "storageAccount",
          name: "stimdemo001",
          replication: "LRS",
          publicAccess: false,
        },
        {
          kind: "networkSecurityGroup",
          name: "nsg-im-lite",
          rules: [],
        },
        {
          kind: "routeTable",
          name: "rt-im-lite",
        },
      ],
      constraints: {
        tags: { owner: "cloudops", env: "student-test", cost: "low" },
        studentSafe: true,
        noCompute: true,
      },
    },
    null,
    2,
  );
}

interface Props {
  subscriptionId: string;
  onApplyToChat: (prompt: string) => void;
  onClose: () => void;
  onOpenSettings: () => void;
  devOpsConfig: DevOpsConfig;
}

export default function DiffPanel({
  subscriptionId,
  onApplyToChat,
  onClose,
  onOpenSettings,
  devOpsConfig,
}: Props) {
  const fileRef = useRef<HTMLInputElement>(null);
  const [editorValue, setEditorValue] = useState<string>("");
  const [showEditor, setShowEditor] = useState(false);
  const [adoLoading, setAdoLoading] = useState(false);
  const [adoError, setAdoError] = useState<string | null>(null);
  const [adoStatus, setAdoStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [applyStatus, setApplyStatus] = useState<string | null>(null);

  const hasAdoConfig = Boolean(
    devOpsConfig.orgUrl &&
    devOpsConfig.project &&
    devOpsConfig.repository &&
    devOpsConfig.pat,
  );

  async function handleLoadFromDevOps() {
    setAdoLoading(true);
    setAdoError(null);
    setAdoStatus(null);
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
        setAdoStatus(
          `No file at ${devOpsConfig.filePath} on ${devOpsConfig.branch}. Starter loaded; Save to DevOps will create it.`,
        );
      } else {
        setAdoError(message);
      }
    } finally {
      setAdoLoading(false);
    }
  }

  async function handleSaveToDevOps() {
    if (!editorValue.trim()) return;
    setAdoLoading(true);
    setAdoError(null);
    setAdoStatus(null);
    try {
      let parsed;
      try {
        parsed = JSON.parse(editorValue);
      } catch {
        setAdoError("Invalid JSON — fix syntax errors before saving.");
        setAdoLoading(false);
        return;
      }
      const pretty = JSON.stringify(parsed, null, 2);
      await saveDesiredState(devOpsConfig, pretty);
      setAdoStatus("Saved to Azure DevOps repo.");
    } catch (e) {
      setAdoError((e as Error).message);
    } finally {
      setAdoLoading(false);
    }
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

  function validateEditorJson() {
    setError(null);
    setApplyStatus(null);

    try {
      JSON.parse(editorValue);
    } catch {
      setError("Invalid JSON — fix syntax errors before running diff.");
      return false;
    }

    return true;
  }

  function handleApplyViaAgent() {
    if (!validateEditorJson()) return;

    const prompt = [
      "Apply the following infrastructure intent using CloudOpsMCP.",
      "Use create_plan first, then deploy_arm_template, then verify with read tools.",
      "Create or update resources requested by the JSON. Do not delete extra resources unless the JSON explicitly asks for deletion.",
      "",
      "Infrastructure JSON:",
      "```json",
      editorValue.trim(),
      "```",
    ].join("\n");
    setApplyStatus("Sent to chat. Watch the InfraMapper Agent panel.");
    onApplyToChat(prompt);
  }
  const canApply =
    editorValue.trim().length > 0 && subscriptionId.trim().length > 0;

  return (
    <div className="flex h-full min-h-0 flex-col overflow-hidden bg-[#0f1421] text-slate-200">
      <div className="sticky top-0 z-20 flex h-11 flex-shrink-0 items-center gap-2 border-b border-slate-700 bg-[#0f1421] px-4">
        <RefreshCw size={13} className="text-cyan-400 flex-shrink-0" />
        <span className="text-xs font-semibold text-slate-300 flex-1 truncate">
          Infrastructure Diff
        </span>
        <button
          onClick={onOpenSettings}
          title={hasAdoConfig ? "DevOps connected" : "Connect Azure DevOps"}
          className={`flex-shrink-0 flex items-center gap-1 text-[10px] px-2 py-0.5 rounded border transition-colors ${
            hasAdoConfig
              ? "border-cyan-700 text-cyan-400 hover:bg-cyan-950/40"
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

      <div className="min-h-0 flex-1 overflow-hidden px-3 py-3 flex flex-col space-y-3">
        {/* Input area */}
        <div className="min-h-0 flex-1 space-y-2 flex flex-col">
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
                  className="flex items-center gap-1.5 text-xs bg-cyan-950/40 hover:bg-cyan-900/40 border border-cyan-700/50 text-cyan-300 px-3 py-1.5 rounded transition-colors disabled:opacity-50"
                >
                  <CloudDownload size={12} />
                  Load from DevOps
                </button>
                <button
                  onClick={handleSaveToDevOps}
                  disabled={adoLoading || !editorValue.trim()}
                  className="flex items-center gap-1.5 text-xs bg-cyan-950/40 hover:bg-cyan-900/40 border border-cyan-700/50 text-cyan-300 px-3 py-1.5 rounded transition-colors disabled:opacity-50"
                >
                  <CloudUpload size={12} />
                  Save to DevOps
                </button>
              </>
            )}
            <input
              ref={fileRef}
              type="file"
              accept=".json"
              className="hidden"
              onChange={handleFile}
            />
          </div>
          {adoStatus && (
            <div className="text-[10px] text-green-400">{adoStatus}</div>
          )}
          {adoError && (
            <div className="text-[10px] text-red-400">{adoError}</div>
          )}

          {showEditor && (
            <textarea
              value={editorValue}
              onChange={(e) => setEditorValue(e.target.value)}
              placeholder={starterIntentJson(subscriptionId)}
              className="min-h-[260px] flex-1 w-full bg-[#0b1018] border border-slate-700 rounded px-3 py-2 text-[11px] text-slate-300 font-mono resize-none outline-none focus:border-cyan-500"
            />
          )}
        </div>

        {error && (
          <div className="text-xs text-red-400 bg-red-950 border border-red-800 rounded px-3 py-2">
            {error}
          </div>
        )}
      </div>

      {canApply && (
        <div className="px-3 py-3 border-t border-slate-700 flex-shrink-0">
          <button
            onClick={handleApplyViaAgent}
            className="w-full flex items-center justify-center gap-1.5 text-xs bg-green-700 hover:bg-green-600 disabled:bg-slate-700 disabled:text-slate-500 text-white px-3 py-2 rounded transition-colors font-medium"
          >
            Apply via Agent
          </button>
          {applyStatus && (
            <p className="text-[10px] text-green-400 mt-1.5 text-center">
              {applyStatus}
            </p>
          )}
          {!applyStatus && (
            <p className="text-[10px] text-slate-600 mt-1.5 text-center">
              Sends the apply request to the chat panel; plans, tools, and
              deployment status appear there
            </p>
          )}
        </div>
      )}
    </div>
  );
}
