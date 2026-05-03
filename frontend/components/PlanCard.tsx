"use client";

import { useState } from "react";
import {
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  ListChecks,
  Loader2,
  RefreshCw,
  ShieldCheck,
  XCircle,
} from "lucide-react";
import { approvePlan, rejectPlan } from "@/lib/api";
import type { Plan, PlanOperation } from "@/lib/types";

const ACTION_STYLES: Record<string, string> = {
  Create: "bg-green-900 text-green-300 border-green-700",
  Update: "bg-amber-900 text-amber-300 border-amber-700",
  Delete: "bg-red-900 text-red-300 border-red-700",
  Deploy: "bg-blue-900 text-blue-300 border-blue-700",
};

interface Props {
  plan: Plan;
  sessionId: string;
  onApproved: () => void;
  onRejected: () => void;
  defaultDetailsOpen?: boolean;
}

export default function PlanCard({ plan, sessionId, onApproved, onRejected, defaultDetailsOpen = true }: Props) {
  const [status, setStatus] = useState<"pending" | "approving" | "rejecting" | "approved" | "rejected">(plan.status ?? "pending");
  const [detailsOpenOverride, setDetailsOpenOverride] = useState<boolean | undefined>();
  const detailsOpen = detailsOpenOverride ?? defaultDetailsOpen;
  const criticRejected = status === "rejected";

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

  return (
    <div className="mt-2 overflow-hidden rounded-lg border border-blue-800/70 bg-[#0f1117] text-xs shadow-lg shadow-black/20">
      <div className="border-b border-blue-900/70 bg-slate-900 px-3 py-2.5">
        <div className="flex items-start gap-2">
          <div className="mt-0.5 flex h-7 w-7 flex-shrink-0 items-center justify-center rounded-md border border-blue-800 bg-blue-950 text-blue-300">
            <ListChecks size={15} />
          </div>
          <div className="min-w-0 flex-1">
            <div className={`text-[10px] font-semibold uppercase tracking-wide ${criticRejected ? "text-red-300" : "text-blue-300"}`}>
              {criticRejected ? "Plan rejected by critic" : "Plan ready for approval"}
            </div>
            <div className="break-words font-semibold leading-snug text-slate-100">{plan.title}</div>
          </div>
        </div>

        <div className="mt-2 flex flex-wrap items-center gap-2">
          <div className={`inline-flex items-center rounded border px-2 py-0.5 text-[10px] font-semibold ${
            plan.riskLevel === "High" ? "border-red-800 bg-red-950 text-red-300" :
            plan.riskLevel === "Medium" ? "border-amber-800 bg-amber-950 text-amber-300" :
            "border-green-800 bg-green-950 text-green-300"
          }`}>
            {plan.riskLevel} blast radius
          </div>
          <div className={`inline-flex items-center gap-1 rounded border px-2 py-0.5 text-[10px] font-semibold ${
            criticRejected ? "border-red-800 bg-red-950 text-red-300" : "border-slate-700 bg-slate-950 text-slate-300"
          }`}>
            <ShieldCheck size={9} />
            {criticRejected ? "Critic rejected" : "Critic passed"}
          </div>
          {(plan.revisionCount ?? 0) > 0 && (
            <div className="inline-flex items-center gap-1 rounded border border-purple-800 bg-purple-950 px-2 py-0.5 text-[10px] font-semibold text-purple-300">
              <RefreshCw size={9} />
              Critic: {plan.revisionCount} revision{plan.revisionCount === 1 ? "" : "s"}
            </div>
          )}
          <div className="inline-flex items-center rounded border border-slate-700 bg-slate-950 px-2 py-0.5 text-[10px] font-semibold text-slate-400">
            {plan.operations.length} operation{plan.operations.length === 1 ? "" : "s"}
          </div>
        </div>
      </div>

      <button
        onClick={() => setDetailsOpenOverride(!detailsOpen)}
        className="flex w-full items-center justify-between gap-2 border-b border-slate-800 bg-slate-950 px-3 py-2 text-slate-300 transition-colors hover:bg-slate-900"
      >
        <span className="flex items-center gap-1.5 text-[11px] font-medium">
          {detailsOpen ? <ChevronDown size={12} /> : <ChevronRight size={12} />}
          Deployment plan
        </span>
        <span className="truncate font-mono text-[10px] text-slate-500">{plan.planId.slice(0, 8)}</span>
      </button>

      {detailsOpen && (
        <div className="divide-y divide-slate-800">
          {plan.operations.map((op: PlanOperation, i: number) => (
            <div key={i} className="flex flex-col gap-1.5 px-3 py-2.5">
              <div className="flex min-w-0 items-center gap-2">
                <span className={`flex-shrink-0 rounded border px-1.5 py-0.5 text-[10px] font-medium ${ACTION_STYLES[op.action] ?? "bg-slate-800 text-slate-300 border-slate-600"}`}>
                  {op.action}
                </span>
                <span className="truncate text-xs font-medium text-slate-100" title={op.resource_name}>{op.resource_name}</span>
              </div>
              <div className="break-words text-[10px] text-slate-500">{op.resource_type}</div>
              {op.resource_group && (
                <div className="text-[10px] text-slate-400">Group: {op.resource_group}</div>
              )}
              {op.details && (
                <div className="break-words text-[10px] leading-relaxed text-slate-400">{op.details}</div>
              )}
            </div>
          ))}
        </div>
      )}

      {plan.criticVerdict && (
        <div className="border-t border-slate-800 bg-slate-950/70 px-3 py-2">
          <div className="mb-1 flex items-center gap-1.5 text-[10px] font-semibold text-slate-300">
            <ShieldCheck size={11} className="text-green-400" />
            Critic assessment
          </div>
          <div className="break-words text-[10px] leading-relaxed text-slate-400">{plan.criticVerdict}</div>
        </div>
      )}

      {plan.estimatedCostNote && (
        <div className="border-t border-slate-700 px-3 py-1.5 text-[10px] text-amber-400">
          {plan.estimatedCostNote}
        </div>
      )}

      <div className="flex gap-2 border-t border-slate-700 bg-slate-950/60 px-3 py-2.5">
        {status === "approved" ? (
          <div className="flex items-center gap-1.5 text-green-400">
            <CheckCircle2 size={13} />
            <span>Approved. Executor starting...</span>
          </div>
        ) : status === "rejected" ? (
          <div className="flex items-center gap-1.5 text-red-400">
            <XCircle size={13} />
            <span>{plan.criticVerdict ? "Rejected. See critic assessment." : "Rejected"}</span>
          </div>
        ) : (
          <>
            <button
              onClick={handleApprove}
              disabled={status === "approving" || status === "rejecting"}
              className="flex items-center gap-1.5 rounded bg-green-700 px-3 py-1.5 font-medium text-white transition-colors hover:bg-green-600 disabled:bg-slate-700"
            >
              {status === "approving" ? <Loader2 size={11} className="animate-spin" /> : <CheckCircle2 size={11} />}
              Approve and run
            </button>
            <button
              onClick={handleReject}
              disabled={status === "approving" || status === "rejecting"}
              className="flex items-center gap-1.5 rounded bg-slate-800 px-3 py-1.5 text-slate-300 transition-colors hover:bg-red-900 hover:text-white disabled:bg-slate-700"
            >
              {status === "rejecting" ? <Loader2 size={11} className="animate-spin" /> : <XCircle size={11} />}
              Cancel
            </button>
          </>
        )}
      </div>
    </div>
  );
}
