"use client";

import { useState } from "react";
import { X, GitBranch, Save } from "lucide-react";

export interface DevOpsConfig {
  orgUrl: string;
  project: string;
  repository: string;
  pat: string;
  branch: string;
  filePath: string;
}

const DEFAULTS: DevOpsConfig = {
  orgUrl: "",
  project: "",
  repository: "",
  pat: "",
  branch: "main",
  filePath: "infra/desired-state.json",
};

interface Props {
  config: DevOpsConfig;
  onChange: (cfg: DevOpsConfig) => void;
  onClose: () => void;
}

export default function DevOpsSettings({ config, onChange, onClose }: Props) {
  const [draft, setDraft] = useState<DevOpsConfig>(config);

  function set(key: keyof DevOpsConfig, value: string) {
    setDraft((d) => ({ ...d, [key]: value }));
  }

  function handleSave() {
    onChange(draft);
    onClose();
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60">
      <div className="w-[440px] bg-[#161b27] border border-slate-700 rounded-xl shadow-2xl overflow-hidden">
        <div className="flex items-center gap-2 px-4 py-3 border-b border-slate-700">
          <GitBranch size={14} className="text-blue-400" />
          <span className="text-sm font-semibold text-slate-200">Azure DevOps Connection</span>
          <button onClick={onClose} className="ml-auto text-slate-500 hover:text-slate-300">
            <X size={14} />
          </button>
        </div>

        <div className="px-4 py-4 space-y-3">
          {[
            { key: "orgUrl" as const, label: "Organization URL", placeholder: "https://dev.azure.com/myorg" },
            { key: "project" as const, label: "Project", placeholder: "MyProject" },
            { key: "repository" as const, label: "Repository", placeholder: "infra-repo" },
            { key: "pat" as const, label: "Personal Access Token", placeholder: "••••••••••••••••", type: "password" },
            { key: "branch" as const, label: "Branch", placeholder: "main" },
            { key: "filePath" as const, label: "File path", placeholder: "infra/desired-state.json" },
          ].map(({ key, label, placeholder, type }) => (
            <div key={key}>
              <label className="block text-[10px] uppercase tracking-wider text-slate-500 mb-1">{label}</label>
              <input
                type={type ?? "text"}
                value={draft[key]}
                onChange={(e) => set(key, e.target.value)}
                placeholder={placeholder}
                className="w-full bg-[#1e293b] border border-slate-600 rounded px-3 py-1.5 text-xs text-slate-200 placeholder-slate-600 outline-none focus:border-blue-500"
              />
            </div>
          ))}

          <div className="pt-1 text-[10px] text-slate-500">
            PAT needs <span className="text-slate-400">Code (Read &amp; Write)</span> scope.
          </div>
        </div>

        <div className="px-4 py-3 border-t border-slate-700 flex gap-2 justify-end">
          <button onClick={onClose} className="text-xs text-slate-400 hover:text-slate-200 px-3 py-1.5">
            Cancel
          </button>
          <button
            onClick={handleSave}
            className="flex items-center gap-1.5 text-xs bg-blue-700 hover:bg-blue-600 text-white px-4 py-1.5 rounded transition-colors"
          >
            <Save size={11} />
            Save
          </button>
        </div>
      </div>
    </div>
  );
}
