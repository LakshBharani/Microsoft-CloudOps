"use client";

import { useState } from "react";
import { CheckCircle2, Loader2, Sparkles, XCircle } from "lucide-react";
import { approvePlan, rejectPlan } from "@/lib/api";
import type { Plan, PlanOperation } from "@/lib/types";

const OP_BADGE: Record<string, string> = {
  Create: "+ ADD",
  Update: "~ UPDATE",
  Delete: "- DELETE",
  Deploy: "> DEPLOY",
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
  onApproved: (planId: string) => void;
  onRejected: (planId: string) => void;
  defaultDetailsOpen?: boolean;
}

export default function PlanCard({ plan, sessionId, onApproved, onRejected }: Props) {
  const [status, setStatus] = useState<"pending" | "approving" | "rejecting" | "approved" | "rejected" | "completed">(
    plan.status === "approved" ? "approved"
      : plan.status === "rejected" ? "rejected"
      : plan.status === "completed" ? "completed"
      : "pending",
  );

  const opCount = plan.operations.length;

  async function handleApprove() {
    setStatus("approving");
    try {
      await approvePlan(plan.planId, sessionId);
      setStatus("approved");
      onApproved(plan.planId);
    } catch {
      setStatus("pending");
    }
  }

  async function handleReject() {
    setStatus("rejecting");
    try {
      await rejectPlan(plan.planId);
      setStatus("rejected");
      onRejected(plan.planId);
    } catch {
      setStatus("pending");
    }
  }

  const busy = status === "approving" || status === "rejecting";

  return (
    <div className="mt-2 overflow-hidden rounded-lg border border-slate-700/80 bg-[#0f172a] text-xs">
      <div className="flex items-center gap-2 border-b border-slate-800 px-3 py-2">
        <Sparkles size={12} className="text-cyan-400" />
        <span className="font-semibold uppercase tracking-wider text-cyan-300 text-[10px]">Plan</span>
        <span className="text-slate-500 text-[10px]">
          · {opCount} op{opCount === 1 ? "" : "s"}
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
          return (
            <div
              key={i}
              className="flex w-full items-center gap-3 rounded px-2 py-1.5"
            >
              <span className="flex-shrink-0 font-mono text-[10px] font-semibold text-slate-500 w-[70px]">
                {label}
              </span>
              <span className="min-w-0 flex-1 font-mono text-[11px] leading-relaxed text-slate-300">
                {describeOp(op)}
              </span>
            </div>
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
        ) : status === "completed" ? (
          <div className="flex items-center gap-1.5 text-green-400 text-[11px]">
            <CheckCircle2 size={12} /> Plan completed
          </div>
        ) : status === "approved" ? (
          <div className="flex items-center gap-1.5 text-green-400 text-[11px]">
            <CheckCircle2 size={12} /> Approved
          </div>
        ) : (
          <>
            <button
              onClick={handleReject}
              disabled={busy}
              className="rounded px-3 py-1.5 text-[11px] text-slate-400 hover:text-slate-200 hover:bg-slate-800 disabled:opacity-40"
            >
              {status === "rejecting" ? "Dismissing..." : "Dismiss"}
            </button>
            <button
              onClick={handleApprove}
              disabled={busy || opCount === 0}
              className="inline-flex items-center gap-1.5 rounded bg-cyan-500 px-3 py-1.5 text-[11px] font-semibold text-slate-900 hover:bg-cyan-400 disabled:bg-slate-700 disabled:text-slate-400"
            >
              {status === "approving" ? (
                <>
                  <Loader2 size={11} className="animate-spin" /> Approving...
                </>
              ) : (
                <>
                  <CheckCircle2 size={11} /> Approve
                </>
              )}
            </button>
          </>
        )}
      </div>
    </div>
  );
}
