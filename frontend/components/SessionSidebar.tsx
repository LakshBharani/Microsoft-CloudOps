"use client";

import { useState, useRef, useEffect } from "react";
import { Plus, MessageSquare, Trash2 } from "lucide-react";
import type { Session } from "@/lib/types";

interface Props {
  sessions: Session[];
  activeId: string;
  onSelect: (id: string) => void;
  onNew: () => void;
  onDelete: (id: string) => void;
  onRename: (id: string, name: string) => void;
}

function SessionItem({ session, isActive, onSelect, onDelete, onRename, showDelete }: {
  session: Session;
  isActive: boolean;
  onSelect: () => void;
  onDelete: () => void;
  onRename: (name: string) => void;
  showDelete: boolean;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(session.name);
  const inputRef = useRef<HTMLInputElement>(null);

  useEffect(() => { setDraft(session.name); }, [session.name]);
  useEffect(() => { if (editing) inputRef.current?.select(); }, [editing]);

  function commit() {
    const trimmed = draft.trim();
    if (trimmed && trimmed !== session.name) onRename(trimmed);
    else setDraft(session.name);
    setEditing(false);
  }

  function handleKeyDown(e: React.KeyboardEvent) {
    if (e.key === "Enter") commit();
    if (e.key === "Escape") { setDraft(session.name); setEditing(false); }
  }

  return (
    <div
      onClick={() => { if (!editing) onSelect(); }}
      className={`group flex items-center gap-2 px-3 py-2 cursor-pointer transition-colors ${
        isActive ? "bg-blue-900/40 border-r-2 border-blue-500" : "hover:bg-slate-800"
      }`}
    >
      <MessageSquare size={12} className={isActive ? "text-blue-400 flex-shrink-0" : "text-slate-500 flex-shrink-0"} />
      {editing ? (
        <input
          ref={inputRef}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={handleKeyDown}
          onClick={(e) => e.stopPropagation()}
          className="flex-1 bg-slate-700 text-xs text-slate-200 px-1 py-0.5 rounded outline-none border border-blue-500 min-w-0"
        />
      ) : (
        <span
          className="flex-1 text-xs text-slate-300 truncate"
          title={session.name}
          onDoubleClick={(e) => { e.stopPropagation(); setEditing(true); }}
        >
          {session.name}
        </span>
      )}
      {showDelete && !editing && (
        <button
          onClick={(e) => { e.stopPropagation(); onDelete(); }}
          className="opacity-0 group-hover:opacity-100 text-slate-500 hover:text-red-400 transition-all flex-shrink-0"
        >
          <Trash2 size={11} />
        </button>
      )}
    </div>
  );
}

export default function SessionSidebar({ sessions, activeId, onSelect, onNew, onDelete, onRename }: Props) {
  return (
    <div className="w-44 flex-shrink-0 flex flex-col bg-[#0f1117] border-l border-slate-700">
      <div className="px-3 py-2 border-b border-slate-700 flex items-center justify-between">
        <span className="text-[10px] uppercase tracking-wider text-slate-500 font-semibold">Sessions</span>
        <button onClick={onNew} className="text-slate-400 hover:text-blue-400 transition-colors" title="New session">
          <Plus size={14} />
        </button>
      </div>

      <div className="flex-1 overflow-y-auto py-1">
        {sessions.map((s) => (
          <SessionItem
            key={s.id}
            session={s}
            isActive={s.id === activeId}
            onSelect={() => onSelect(s.id)}
            onDelete={() => onDelete(s.id)}
            onRename={(name) => onRename(s.id, name)}
            showDelete={sessions.length > 1}
          />
        ))}
      </div>
    </div>
  );
}
