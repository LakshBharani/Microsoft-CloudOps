"use client";

import { useState } from "react";
import { Plus, X } from "lucide-react";
import type { Session } from "@/lib/types";

interface Props {
  sessions: Session[];
  activeId: string;
  onSelect: (id: string) => void;
  onNew: () => void;
  onDelete: (id: string) => void;
  onRename: (id: string, name: string) => void;
}

export default function SessionTabs({ sessions, activeId, onSelect, onNew, onDelete, onRename }: Props) {
  const [editingId, setEditingId] = useState<string | null>(null);
  const [draft, setDraft] = useState("");

  function beginRename(session: Session) {
    setEditingId(session.id);
    setDraft(session.name);
  }

  function commitRename(session: Session) {
    const trimmed = draft.trim();
    if (trimmed && trimmed !== session.name) onRename(session.id, trimmed);
    setEditingId(null);
  }

  return (
    <div className="flex min-h-10 items-center gap-1 overflow-x-auto border-b border-slate-700 bg-[#101621] px-2">
      {sessions.map((session) => {
        const active = session.id === activeId;
        return (
          <button
            key={session.id}
            onClick={() => onSelect(session.id)}
            onDoubleClick={() => beginRename(session)}
            className={`group flex max-w-44 items-center gap-2 rounded-t px-3 py-2 text-xs transition-colors ${
              active ? "bg-[#161b27] text-slate-100" : "text-slate-500 hover:bg-slate-900 hover:text-slate-300"
            }`}
            title={session.name}
          >
            {editingId === session.id ? (
              <input
                value={draft}
                onChange={(e) => setDraft(e.target.value)}
                onClick={(e) => e.stopPropagation()}
                onBlur={() => commitRename(session)}
                onKeyDown={(e) => {
                  if (e.key === "Enter") commitRename(session);
                  if (e.key === "Escape") setEditingId(null);
                }}
                className="w-28 rounded border border-blue-700 bg-slate-950 px-1 py-0.5 text-xs text-slate-200 outline-none"
                autoFocus
              />
            ) : (
              <span className="truncate">{session.name}</span>
            )}
            {sessions.length > 1 && (
              <span
                role="button"
                tabIndex={0}
                onClick={(e) => { e.stopPropagation(); onDelete(session.id); }}
                className="opacity-50 hover:text-red-300 group-hover:opacity-100"
              >
                <X size={11} />
              </span>
            )}
          </button>
        );
      })}
      <button onClick={onNew} className="rounded p-1.5 text-slate-500 hover:bg-slate-900 hover:text-blue-300" title="New session">
        <Plus size={14} />
      </button>
    </div>
  );
}
