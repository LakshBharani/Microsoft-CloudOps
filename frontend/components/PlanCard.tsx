"use client";

import { useState } from "react";
import { CheckCircle2, Loader2, Sparkles, XCircle } from "lucide-react";
import { approvePlan, rejectPlan } from "@/lib/api";
import type { Plan, PlanOperation } from "@/lib/types";

const OP_BADGE: Record<string, string> = {
  Create: "+ ADD",
  Update: "~ UPDATE",
  Delete: "- DELETE",
  Deploy: "» DEPLOY",
};

function badge(action: string) {
  const key = action.charAt(0).toUpperCase() + action.slice(1).toLowerCase();
  return OP_BADGE[key] ?? action.toUpperCase();
}

function describeOp(op: PlanOperation) {
  const detail = typeof op.details === "string" ? op.details : op.details ? JSON.stringify(op.details) : "";
  const head = op.resource_name || op.resource_type;
  return detail ? `${head} — ${detail}` : head;
}

interface Props {
  plan: Plan;
  sessionId: string;
  onApproved: () => void;
  onRejected: () => void;
  defaultDetailsOpen?: boolean;
}

export default function PlanCard({ plan, sessionId, onApproved, onRejected }: Props) {
  const [status, setStatus] = useState<"pending" | "approving" | "rejecting" | "approved" | "rejected">(
    plan.status === "approved" ? "approved" : plan.status === "rejected" ? "rejected" : "pending",
  );
  const [disabled, setDisabled] = useState<Set<number>>(new Set());

  const enabledCount = plan.operations.length - disabled.size;

  function toggle(i: number) {
    if (status !== "pending") return;
    setDisabled((prev) => {
      const next = new Set(prev);
      if (next.has(i)) next.delete(i);
      else next.add(i);
      return next;
    });
  }

  async function handleApprove() {
    setStatus("approving");
    try {
      await approvePlan(plan.planId, sessionId);
      setStatus("approved");
      onApproved();
    } catch {
      setStatus("pending");
    }
  }

  async function handleReject() {
    setStatus("rejecting");
    try {
      await rejectPlan(plan.planId);
      setStatus("rejected");
      onRejected();
    } catch {
      setStatus("pending");
    }
  }

  const running = status === "approving" || status === "approved";

  return (
    <div className="mt-2 overflow-hidden rounded-lg border border-slate-700/80 bg-[#0f172a] text-xs">
      <div className="flex items-center gap-2 border-b border-slate-800 px-3 py-2">
        <Sparkles size={12} className="text-cyan-400" />
        <span className="font-semibold uppercase tracking-wider text-cyan-300 text-[10px]">Plan</span>
        <span className="text-slate-500 text-[10px]">
          · {enabledCount} of {plan.operations.length} ops enabled
        </span>
        <span
          className={`ml-auto rounded border px-1.5 py-0.5 text-[9px] font-semibold uppercase tracking-wide ${
            plan.riskLevel === "High"
              ? "border-red-800 bg-red-950 text-red-300"
              : plan.riskLevel === "Medium"
                ? "border-amber-800 bg-amber-950 text-amber-300"
                : "border-green-800 bg-green-950 text-green-300"
          }`}
        >
          {plan.riskLevel} blast
        </span>
      </div>

      {plan.title && (
        <div className="px-3 py-2 text-[11px] leading-relaxed text-slate-200 border-b border-slate-800">
          {plan.title}
        </div>
      )}

      <div className="px-1 py-1">
        {plan.operations.map((op, i) => {
          const label = badge(op.action);
          const isDisabled = disabled.has(i);
          return (
            <button
              key={i}
              onClick={() => toggle(i)}
              disabled={status !== "pending"}
              className={`flex w-full items-center gap-3 rounded px-2 py-1.5 text-left transition-colors ${
                status === "pending" ? "hover:bg-slate-900/40" : ""
              } ${isDisabled ? "opacity-40" : ""}`}
            >
              <span
                className={`inline-flex h-3.5 w-3.5 flex-shrink-0 items-center justify-center rounded-sm border ${
                  isDisabled
                    ? "border-slate-600"
                    : "border-slate-400 bg-slate-400"
                }`}
              >
                {!isDisabled && (
                  <CheckCircle2 size={9} className="text-slate-900" strokeWidth={3} />
                )}
              </span>
              <span className="flex-shrink-0 font-mono text-[10px] font-semibold text-slate-500 w-[70px]">
                {label}
              </span>
              <span className="min-w-0 flex-1 truncate font-mono text-[11px] text-slate-300">
                {describeOp(op)}
              </span>
            </button>
          );
        })}
      </div>

      {plan.criticVerdict && (
        <div className="border-t border-slate-800 px-3 py-1.5 text-[10px] text-slate-500">
          {plan.criticVerdict}
        </div>
      )}
      {plan.estimatedCostNote && (
        <div className="border-t border-slate-800 px-3 py-1.5 text-[10px] text-amber-400">
          {plan.estimatedCostNote}
        </div>
      )}

      <div className="flex items-center justify-end gap-2 border-t border-slate-800 bg-slate-950/60 px-3 py-2">
        {status === "rejected" ? (
          <div className="flex items-center gap-1.5 text-red-400 text-[11px]">
            <XCircle size={12} /> Dismissed
          </div>
        ) : (
          <>
            <button
              onClick={handleReject}
              disabled={running || status === "rejecting"}
              className="rounded px-3 py-1.5 text-[11px] text-slate-400 hover:text-slate-200 hover:bg-slate-800 disabled:opacity-40"
            >
              {status === "rejecting" ? "Dismissing..." : "Dismiss"}
            </button>
            <button
              onClick={handleApprove}
              disabled={running || status === "rejecting" || enabledCount === 0}
              className="inline-flex items-center gap-1.5 rounded bg-cyan-500 px-3 py-1.5 text-[11px] font-semibold text-slate-900 hover:bg-cyan-400 disabled:bg-slate-700 disabled:text-slate-400"
            >
              {status === "approving" ? (
                <>
                  <Loader2 size={11} className="animate-spin" /> Starting...
                </>
              ) : status === "approved" ? (
                <>
                  <Loader2 size={11} className="animate-spin" /> Running...
                </>
              ) : (
                <>
                  <CheckCircle2 size={11} /> Run plan
                </>
              )}
            </button>
          </>
        )}
      </div>
    </div>
  );
}
