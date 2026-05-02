"use client";

import { X } from "lucide-react";
import type { ResourceNode } from "@/lib/types";
import { getResourceMeta } from "@/lib/resourceMeta";

interface Props {
  node: ResourceNode | null;
  onClose: () => void;
}

export default function ResourcePanel({ node, onClose }: Props) {
  if (!node) return null;
  const meta = getResourceMeta(node.type);

  return (
    <div className="w-80 flex-shrink-0 bg-[#161b27] border-l border-slate-700 flex flex-col overflow-hidden">
      <div className="flex items-center justify-between px-4 py-3 border-b border-slate-700">
        <div>
          <span className="text-xs font-semibold px-1.5 py-0.5 rounded mr-2" style={{ color: meta.color, background: meta.bg }}>
            {meta.label}
          </span>
          <span className="text-sm font-medium text-white">{node.name}</span>
        </div>
        <button onClick={onClose} className="text-slate-400 hover:text-white">
          <X size={16} />
        </button>
      </div>

      <div className="flex-1 overflow-y-auto p-4 space-y-4 text-xs">
        <Section title="Identity">
          <Row label="Name" value={node.name} />
          <Row label="Type" value={node.type} />
          <Row label="Location" value={node.location} />
          <Row label="Resource Group" value={node.resourceGroup} />
        </Section>

        {node.tags && Object.keys(node.tags).length > 0 && (
          <Section title="Tags">
            {Object.entries(node.tags).map(([k, v]) => (
              <Row key={k} label={k} value={v} />
            ))}
          </Section>
        )}

        {node.skuJson && (
          <Section title="SKU">
            <pre className="text-slate-300 bg-slate-900 rounded p-2 overflow-x-auto text-[10px]">
              {JSON.stringify(JSON.parse(node.skuJson), null, 2)}
            </pre>
          </Section>
        )}

        <Section title="Resource ID">
          <p className="text-slate-400 break-all leading-relaxed">{node.id}</p>
        </Section>
      </div>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div>
      <h3 className="text-[10px] uppercase tracking-wider text-slate-500 mb-2">{title}</h3>
      <div className="space-y-1">{children}</div>
    </div>
  );
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex gap-2">
      <span className="text-slate-500 w-28 flex-shrink-0">{label}</span>
      <span className="text-slate-200 break-all">{value}</span>
    </div>
  );
}
