"use client";

import { useState } from "react";
import { CheckCircle2, XCircle, Loader2 } from "lucide-react";
import { approvePlan, rejectPlan } from "@/lib/api";
import type { Plan, PlanOperation } from "@/lib/types";

const ACTION_STYLES: Record<string, string> = {
  Create: "bg-green-900 text-green-300 border-green-700",
  Update: "bg-amber-900 text-amber-300 border-amber-700",
  Delete: "bg-red-900 text-red-300 border-red-700",
  Deploy: "bg-blue-900 text-blue-300 border-blue-700",
};

const RISK_STYLES: Record<string, string> = {
  Low: "text-green-400",
  Medium: "text-amber-400",
  High: "text-red-400",
};

interface Props {
  plan: Plan;
  sessionId: string;
  onApproved: () => void;
  onRejected: () => void;
}

export default function PlanCard({ plan, sessionId, onApproved, onRejected }: Props) {
  const [status, setStatus] = useState<"pending" | "approving" | "rejecting" | "approved" | "rejected">("pending");

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
    <div className="mt-2 rounded-lg border border-slate-600 bg-[#0f1117] overflow-hidden text-xs">
      <div className="px-3 py-2 bg-slate-800 border-b border-slate-600">
        <div className="font-semibold text-slate-200">{plan.title}</div>
        <div className={`inline-flex items-center mt-1 px-2 py-0.5 rounded-full text-[10px] font-semibold border ${
          plan.riskLevel === "High" ? "bg-red-950 border-red-700 text-red-400" :
          plan.riskLevel === "Medium" ? "bg-amber-950 border-amber-700 text-amber-400" :
          "bg-green-950 border-green-700 text-green-400"
        }`}>
          {plan.riskLevel} Risk
        </div>
      </div>

      <div className="divide-y divide-slate-800">
        {plan.operations.map((op: PlanOperation, i: number) => (
          <div key={i} className="px-3 py-2 flex flex-col gap-1">
            <div className="flex items-center gap-2">
              <span className={`px-1.5 py-0.5 rounded border text-[10px] font-medium flex-shrink-0 ${ACTION_STYLES[op.action] ?? "bg-slate-800 text-slate-300 border-slate-600"}`}>
                {op.action}
              </span>
              <span className="text-xs text-slate-200 font-medium truncate">{op.resource_name}</span>
            </div>
            <div className="text-[10px] text-slate-500">{op.resource_type}</div>
            {op.resource_group && (
              <div className="text-[10px] text-slate-400">Group: {op.resource_group}</div>
            )}
            {op.details && (
              <div className="text-[10px] text-slate-400 leading-relaxed">{op.details}</div>
            )}
          </div>
        ))}
      </div>

      {plan.estimatedCostNote && (
        <div className="px-3 py-1.5 border-t border-slate-700 text-amber-400 text-[10px]">
          {plan.estimatedCostNote}
        </div>
      )}

      <div className="flex gap-2 px-3 py-2 border-t border-slate-700">
        {status === "approved" ? (
          <div className="flex items-center gap-1.5 text-green-400">
            <CheckCircle2 size={13} />
            <span>Approved — executing...</span>
          </div>
        ) : status === "rejected" ? (
          <div className="flex items-center gap-1.5 text-red-400">
            <XCircle size={13} />
            <span>Rejected</span>
          </div>
        ) : (
          <>
            <button
              onClick={handleApprove}
              disabled={status === "approving" || status === "rejecting"}
              className="flex items-center gap-1.5 bg-green-700 hover:bg-green-600 disabled:bg-slate-700 text-white px-3 py-1 rounded transition-colors"
            >
              {status === "approving" ? <Loader2 size={11} className="animate-spin" /> : <CheckCircle2 size={11} />}
              Approve
            </button>
            <button
              onClick={handleReject}
              disabled={status === "approving" || status === "rejecting"}
              className="flex items-center gap-1.5 bg-slate-700 hover:bg-red-800 disabled:bg-slate-700 text-slate-300 hover:text-white px-3 py-1 rounded transition-colors"
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
