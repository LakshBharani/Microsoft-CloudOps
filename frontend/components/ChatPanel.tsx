"use client";

import { useState, useRef, useEffect } from "react";
import { Send, Bot, Wrench, CheckCircle2, XCircle, X, ChevronDown, ChevronRight, Cpu, AlertTriangle } from "lucide-react";
import { streamChat } from "@/lib/api";
import type { ChatMessage, ToolCall, Plan, ResourceNode, AgentActivityItem, ClarifyingQuestion } from "@/lib/types";
import ReactMarkdown from "react-markdown";
import PlanCard from "./PlanCard";
import QuestionCard from "./QuestionCard";

interface Props {
  sessionId: string;
  subscriptionId: string;
  messages: ChatMessage[];
  onMessagesChange: (msgs: ChatMessage[] | ((prev: ChatMessage[]) => ChatMessage[])) => void;
  onSessionIdSet: (id: string) => void;
  onDeploymentComplete: () => void;
  tokenUsage: { input: number; output: number };
  onTokenUsage: (u: { input: number; output: number }) => void;
  contextNodes?: ResourceNode[];
  onRemoveContext?: (id: string) => void;
  syntheticPrompt?: string | null;
  onSyntheticPromptConsumed?: () => void;
}

const TOOL_LABELS: Record<string, string> = {
  get_infrastructure_graph: "Reading infrastructure graph",
  get_resource: "Reading resource",
  get_deployment_status: "Checking deployment status",
  create_plan: "Creating plan",
  deploy_arm_template: "Deploying ARM template",
  apply_resource_mutation: "Applying resource mutation",
  investigate_infrastructure: "Investigating infrastructure",
  plan_deployment: "Planning deployment",
  critique_plan: "Critiquing plan",
  ask_clarifying_question: "Asking clarification",
  execute_plan: "Executing plan",
  reflect_on_deployment: "Reflecting on deployment",
  get_lessons: "Referencing lessons learned",
};

const AGENT_LABELS: Record<string, string> = {
  investigate_infrastructure: "Investigator",
  plan_deployment: "Planner",
  critique_plan: "Critic",
  ask_clarifying_question: "Questioner",
  execute_plan: "Executor",
  reflect_on_deployment: "Reflector",
};

const ORCHESTRATOR_MODEL = "claude-haiku-4-5";

function formatModelName(model?: string) {
  return model?.replace(/^claude-/, "").replace(/-/g, " ");
}

function collapsePreviousAgentRich(messages: ChatMessage[]) {
  return messages.map((msg) =>
    msg.role === "agent" && ((msg.activities?.length ?? 0) > 0 || (msg.plans?.length ?? 0) > 0 || msg.plan)
      ? { ...msg, richCollapsed: true }
      : msg
  );
}

function cleanAgentText(value?: string) {
  return value
    ?.replace(/\*\*/g, "")
    .replace(/[\p{Emoji_Presentation}\p{Extended_Pictographic}\uFE0F]/gu, "")
    .replace(/\s+/g, " ")
    .trim();
}

function isOperationResult(content: string) {
  const normalized = cleanAgentText(content.replace(/\r/g, "")) ?? "";
  return /\b(complete|completed|succeeded|successfully|deleted|created|updated|deployed)\b/i.test(normalized);
}

function formatWorkedDuration(ms: number) {
  const minutes = Math.max(1, Math.ceil(ms / 60000));
  return `${minutes}m`;
}

function getWorkedDuration(msg: ChatMessage) {
  if (msg.isStreaming || !msg.activities || msg.activities.length === 0) return null;

  const startedAt = Math.min(...msg.activities.map((item) => item.startedAt ?? item.endedAt ?? Infinity));
  const endedAt = Math.max(...msg.activities.map((item) => item.endedAt ?? item.startedAt ?? -Infinity));

  if (!Number.isFinite(startedAt) || !Number.isFinite(endedAt) || endedAt < startedAt) return null;
  return formatWorkedDuration(endedAt - startedAt);
}

function ToolCallList({ toolCalls }: { toolCalls: ToolCall[] }) {
  if (toolCalls.length === 0) return null;

  return (
    <div className="mb-2 space-y-1 border-b border-slate-700 pb-2">
      {toolCalls.map((tc, i) => (
        <div key={i} className="flex items-center gap-1.5 text-[10px]">
          {!tc.done ? (
            <Wrench size={11} className="text-blue-400 animate-pulse flex-shrink-0" />
          ) : tc.success === false ? (
            <XCircle size={11} className="text-red-400 flex-shrink-0" />
          ) : (
            <CheckCircle2 size={11} className="text-green-500 flex-shrink-0" />
          )}
          <span className={tc.success === false ? "text-red-400" : "text-slate-400"}>
            {TOOL_LABELS[tc.tool] ?? tc.tool}
          </span>
        </div>
      ))}
    </div>
  );
}

function ActivityTimeline({ activities, defaultOpen = true }: { activities?: AgentActivityItem[]; defaultOpen?: boolean }) {
  const [openOverride, setOpenOverride] = useState<boolean | undefined>();
  const open = openOverride ?? defaultOpen;

  if (!activities || activities.length === 0) return null;

  const roots = activities.filter((item) => !item.parentId);
  const children = new Map<string, AgentActivityItem[]>();
  activities.forEach((item) => {
    if (!item.parentId) return;
    children.set(item.parentId, [...(children.get(item.parentId) ?? []), item]);
  });

  function icon(item: AgentActivityItem) {
    if (item.status === "running") return <Cpu size={11} className="animate-pulse text-blue-400" />;
    if (item.status === "failed") return <XCircle size={11} className="text-red-400" />;
    if (item.status === "rejected") return <AlertTriangle size={11} className="text-amber-400" />;
    return <CheckCircle2 size={11} className="text-green-400" />;
  }

  function label(item: AgentActivityItem) {
    return item.agent
      ? (AGENT_LABELS[item.agent] ?? item.agent)
      : item.tool
        ? (TOOL_LABELS[item.tool] ?? item.tool)
        : item.summary;
  }

  return (
    <div className="mb-2 rounded border border-slate-700 bg-slate-950/40">
      <button
        onClick={() => setOpenOverride(!open)}
        className="flex w-full items-center gap-1.5 px-2 py-1.5 text-left text-[10px] text-slate-400 hover:bg-slate-900"
      >
        {open ? <ChevronDown size={10} /> : <ChevronRight size={10} />}
        <Cpu size={10} className="text-blue-400" />
        <span>Agents at work — {activities.length} event{activities.length === 1 ? "" : "s"}</span>
      </button>
      {open && (
        <div className="space-y-1 border-t border-slate-800 px-2 py-2">
          {roots.map((item) => (
            <div key={item.id} className="rounded border border-slate-800 bg-slate-900/40 px-2 py-1.5">
              <div className="flex items-start gap-1.5 text-[10px]">
                {icon(item)}
                <div className="min-w-0 flex-1">
                  <div className="flex flex-wrap items-center gap-1.5">
                    <span className="font-medium text-slate-200">{label(item)}</span>
                    {item.model && (
                      <span className="text-slate-500">· {formatModelName(item.model)}</span>
                    )}
                  </div>
                  <div className="text-slate-500">{item.message ?? item.summary}</div>
                  {item.errorType && <div className="text-red-300">Error: {item.errorType}</div>}
                </div>
              </div>
              {(children.get(item.id) ?? []).length > 0 && (
                <div className="mt-1.5 space-y-1 border-l border-slate-700 pl-2">
                  {(children.get(item.id) ?? []).map((child) => (
                    <div key={child.id} className="flex items-start gap-1.5 text-[10px] text-slate-500">
                      {icon(child)}
                      <div className="min-w-0">
                        <span className="font-medium text-slate-300">{label(child)}</span>
                        {child.model && (
                          <span className="ml-1 text-slate-600">· {formatModelName(child.model)}</span>
                        )}
                        <span className="ml-1 text-slate-500">{child.message ?? child.summary}</span>
                      </div>
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

function AgentMessage({
  msg,
  sessionId,
  onApproved,
  onRejected,
  onQuestionAnswered,
}: {
  msg: ChatMessage;
  sessionId: string;
  onApproved: () => void;
  onRejected: () => void;
  onQuestionAnswered: (questionId: string, answer: string) => void;
}) {
  const richOpen = msg.richCollapsed !== true;
  const operationResult = msg.content ? isOperationResult(msg.content) : false;
  const workedDuration = getWorkedDuration(msg);

  return (
    <div className="w-full space-y-2 rounded-lg border border-slate-800 bg-[#1e293b] px-3 py-2 text-xs leading-relaxed text-slate-200">
      <div className="flex items-center gap-1.5 text-[10px] font-semibold uppercase tracking-wide text-blue-300">
        <Bot size={11} />
        <span>InfraMapper Agent</span>
        <span className="normal-case tracking-normal text-slate-500">· {formatModelName(ORCHESTRATOR_MODEL)}</span>
        {workedDuration && (
          <span className="ml-auto normal-case tracking-normal text-slate-500">Worked for {workedDuration}</span>
        )}
      </div>
      <div className="break-words">
        <ActivityTimeline activities={msg.activities} defaultOpen={richOpen} />
        {msg.toolCalls && msg.toolCalls.length > 0 && (
          <ToolCallList toolCalls={msg.toolCalls} />
        )}
        {(msg.plans ?? (msg.plan ? [msg.plan] : [])).map((plan) => (
          <PlanCard
            key={plan.planId}
            plan={plan}
            sessionId={sessionId}
            onApproved={onApproved}
            onRejected={onRejected}
            defaultDetailsOpen={richOpen}
          />
        ))}
        {(msg.questions ?? (msg.question ? [msg.question] : [])).map((question) => (
          <QuestionCard
            key={question.questionId}
            question={question}
            sessionId={sessionId}
            onAnswered={onQuestionAnswered}
          />
        ))}
        {msg.content && !operationResult && !msg.plan && !(msg.plans && msg.plans.length > 0) && !(msg.questions && msg.questions.length > 0) && (
          <ReactMarkdown
            components={{
              p: ({ children }) => <p className="mb-1 last:mb-0">{children}</p>,
              ul: ({ children }) => <ul className="mb-1 space-y-0.5">{children}</ul>,
              ol: ({ children }) => <ol className="list-decimal pl-4 mb-1">{children}</ol>,
              li: ({ children }) => <li className="mb-0.5">{children}</li>,
              strong: ({ children }) => <strong className="text-white font-semibold">{children}</strong>,
              code: ({ children }) => <code className="bg-slate-900 px-1 rounded text-[10px] text-blue-300 break-all">{children}</code>,
              pre: ({ children }) => <pre className="bg-slate-900 rounded p-2 text-[10px] text-blue-300 overflow-x-auto whitespace-pre-wrap break-all my-1">{children}</pre>,
              table: ({ children }) => <table className="border-collapse w-full my-2 text-[11px]">{children}</table>,
              thead: ({ children }) => <thead className="bg-slate-800">{children}</thead>,
              th: ({ children }) => <th className="border border-slate-600 px-2 py-1 text-left text-slate-300">{children}</th>,
              td: ({ children }) => <td className="border border-slate-600 px-2 py-1">{children}</td>,
            }}
          >
            {cleanAgentText(msg.content)}
          </ReactMarkdown>
        )}
        {msg.isStreaming && !msg.content && !msg.plan && (
          <span className="text-slate-500 italic text-[10px]">Thinking...</span>
        )}
      </div>
    </div>
  );
}

export default function ChatPanel({
  sessionId,
  subscriptionId,
  messages,
  onMessagesChange,
  onSessionIdSet,
  onDeploymentComplete,
  tokenUsage,
  onTokenUsage,
  contextNodes = [],
  onRemoveContext,
  syntheticPrompt,
  onSyntheticPromptConsumed,
}: Props) {
  const [input, setInput] = useState("");
  const [loading, setLoading] = useState(false);
  const bottomRef = useRef<HTMLDivElement>(null);

  function handleActivityStart(data: Extract<import("@/lib/types").AgentStreamEvent, { type: "activity_start" }>["data"]) {
    const item: AgentActivityItem = {
      id: data.id,
      parentId: data.parent_id ?? undefined,
      kind: data.kind,
      agent: data.agent ?? undefined,
      tool: data.tool ?? undefined,
      model: data.model ?? undefined,
      status: data.status,
      summary: data.summary,
      detailPreview: data.detail_preview ?? undefined,
      errorType: data.error_type ?? undefined,
      message: data.message ?? undefined,
      startedAt: Date.now(),
    };
    onMessagesChange((prev: ChatMessage[]) => {
      const msgs = [...prev];
      const last = msgs[msgs.length - 1];
      const activities = last.activities ?? [];
      const existing = activities.find((p) => p.id === item.id);
      msgs[msgs.length - 1] = {
        ...last,
        activities: existing
          ? activities.map((p) => p.id === item.id ? { ...p, ...item, startedAt: p.startedAt ?? item.startedAt } : p)
          : [...activities, item],
      };
      return msgs;
    });
  }

  function handleActivityEnd(data: Extract<import("@/lib/types").AgentStreamEvent, { type: "activity_end" }>["data"]) {
    onMessagesChange((prev: ChatMessage[]) => {
      const msgs = [...prev];
      const last = msgs[msgs.length - 1];
      const activities = last.activities ?? [];
      const existing = activities.find((p) => p.id === data.id);
      const patch: Partial<AgentActivityItem> = {
        status: data.status,
        summary: data.summary,
        detailPreview: data.detail_preview ?? existing?.detailPreview,
        errorType: data.error_type ?? existing?.errorType,
        message: data.message ?? existing?.message,
        endedAt: Date.now(),
      };
      const fallback: AgentActivityItem = {
        id: data.id,
        parentId: data.parent_id ?? undefined,
        kind: data.kind ?? "tool",
        agent: data.agent ?? undefined,
        tool: data.tool ?? undefined,
        model: data.model ?? undefined,
        status: data.status,
        summary: data.summary,
        detailPreview: data.detail_preview ?? undefined,
        errorType: data.error_type ?? undefined,
        message: data.message ?? undefined,
        endedAt: Date.now(),
      };
      msgs[msgs.length - 1] = {
        ...last,
        activities: existing
          ? activities.map((p) => p.id === data.id ? { ...p, ...patch } : p)
          : [...activities, fallback],
      };
      return msgs;
    });
  }

  function appendQuestion(question: ClarifyingQuestion) {
    onMessagesChange((prev: ChatMessage[]) => {
      const msgs = [...prev];
      const last = msgs[msgs.length - 1];
      msgs[msgs.length - 1] = {
        ...last,
        question,
        questions: [...(last.questions ?? []), question],
      };
      return msgs;
    });
  }

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  useEffect(() => {
    if (syntheticPrompt && !loading) {
      onSyntheticPromptConsumed?.();
      handleSendText(syntheticPrompt);
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [syntheticPrompt]);

  async function handleSendText(overrideText: string) {
    const text = overrideText.trim();
    if (!text || loading) return;

    const contextBlock = contextNodes.length > 0
      ? `[Selected resources for context:\n${contextNodes.map(n =>
          `- ${n.name} (${n.type}, group: ${n.resourceGroup}, location: ${n.location}, id: ${n.id})`
        ).join("\n")}]\n\n`
      : "";
    const fullText = contextBlock + text;

    setInput("");
    contextNodes.forEach((n) => onRemoveContext?.(n.id));
    const newMessages: ChatMessage[] = [
      ...collapsePreviousAgentRich(messages),
      { role: "user", content: text },
      { role: "agent", content: "", toolCalls: [], isStreaming: true, richCollapsed: false },
    ];
    onMessagesChange(newMessages);
    setLoading(true);

    try {
      for await (const evt of streamChat(fullText, subscriptionId, sessionId)) {
        if (evt.type === "tool_call") {
          onSessionIdSet(evt.data.session_id);
          onMessagesChange((prev: ChatMessage[]) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              toolCalls: [...(last.toolCalls ?? []), { tool: evt.data.tool, done: false }],
            };
            return msgs;
          });
        } else if (evt.type === "tool_result") {
          onMessagesChange((prev: ChatMessage[]) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              toolCalls: last.toolCalls?.map((tc) =>
                tc.tool === evt.data.tool && !tc.done
                  ? { ...tc, done: true, success: evt.data.success }
                  : tc
              ),
            };
            return msgs;
          });
        } else if (evt.type === "plan") {
          onSessionIdSet(evt.data.session_id);
          const plan: Plan = {
            planId: evt.data.plan_id,
            title: evt.data.title,
            operations: evt.data.operations,
            riskLevel: evt.data.risk_level as Plan["riskLevel"],
            estimatedCostNote: evt.data.estimated_cost_note,
            criticVerdict: evt.data.critic_verdict,
            revisionCount: evt.data.revision_count,
            status: evt.data.status ?? "pending",
          };
          onMessagesChange((prev: ChatMessage[]) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              plan,
              plans: [...(last.plans ?? []), plan],
            };
            return msgs;
          });
        } else if (evt.type === "question") {
          onSessionIdSet(evt.data.session_id);
          appendQuestion({
            questionId: evt.data.question_id,
            title: evt.data.title,
            prompt: evt.data.prompt,
            options: evt.data.options,
            defaultValue: evt.data.default_value ?? undefined,
            allowCustom: evt.data.allow_custom,
            category: evt.data.category ?? undefined,
            confirmationScope: evt.data.confirmation_scope ?? undefined,
            originatingAgent: evt.data.originating_agent ?? undefined,
            status: "pending",
          });
        } else if (evt.type === "activity_start") {
          handleActivityStart(evt.data);
        } else if (evt.type === "activity_end") {
          handleActivityEnd(evt.data);
        } else if (evt.type === "usage") {
          onTokenUsage({ input: evt.data.input_tokens, output: evt.data.output_tokens });
        } else if (evt.type === "reply") {
          onSessionIdSet(evt.data.session_id);
          onMessagesChange((prev: ChatMessage[]) => {
            const msgs = [...prev];
            msgs[msgs.length - 1] = {
              ...msgs[msgs.length - 1],
              content: evt.data.content,
              isStreaming: false,
            };
            return msgs;
          });
          const lower = evt.data.content.toLowerCase();
          if (lower.includes("succeeded") || lower.includes("deployed") || lower.includes("created")) {
            onDeploymentComplete();
          }
        } else if (evt.type === "error") {
          onMessagesChange((prev: ChatMessage[]) => {
            const msgs = [...prev];
            msgs[msgs.length - 1] = {
              ...msgs[msgs.length - 1],
              content: `Error: ${evt.data.message}`,
              isStreaming: false,
            };
            return msgs;
          });
        }
      }
    } catch (err) {
      onMessagesChange((prev: ChatMessage[]) => {
        const msgs = [...prev];
        msgs[msgs.length - 1] = {
          ...msgs[msgs.length - 1],
          content: `Error: ${(err as Error).message}`,
          isStreaming: false,
        };
        return msgs;
      });
    } finally {
      setLoading(false);
    }
  }

  function handleKeyDown(e: React.KeyboardEvent) {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      handleSend();
    }
  }

  function handleSend() {
    handleSendText(input);
  }

  function handlePlanApproved() {
    // After approval, the backend injects an executor-specific resume message.
    setTimeout(() => {
      handleResumeAfterApproval();
    }, 300);
  }

  function handleQuestionAnswered(questionId: string, answer: string) {
    const next = messages.map((msg) => {
      const questions = msg.questions?.map((q) =>
        q.questionId === questionId ? { ...q, status: "answered" as const, answer } : q
      );
      return {
        ...msg,
        question: msg.question?.questionId === questionId
          ? { ...msg.question, status: "answered" as const, answer }
          : msg.question,
        questions,
      };
    });

    onMessagesChange(next);
    const pending = next.flatMap((m) => m.questions ?? (m.question ? [m.question] : []))
      .some((q) => q.status !== "answered" && !q.answer);

    if (!pending) {
      setTimeout(() => {
        handleResumeAfterQuestion();
      }, 300);
    }
  }

  async function handleResumeAfterQuestion() {
    setLoading(true);
    onMessagesChange((prev) => [
      ...collapsePreviousAgentRich(prev),
      { role: "agent", content: "", toolCalls: [], isStreaming: true, richCollapsed: false },
    ]);

    try {
      for await (const evt of streamChat("continue with clarification answer", subscriptionId, sessionId)) {
        if (evt.type === "activity_start") {
          handleActivityStart(evt.data);
        } else if (evt.type === "activity_end") {
          handleActivityEnd(evt.data);
        } else if (evt.type === "question") {
          appendQuestion({
            questionId: evt.data.question_id,
            title: evt.data.title,
            prompt: evt.data.prompt,
            options: evt.data.options,
            defaultValue: evt.data.default_value ?? undefined,
            allowCustom: evt.data.allow_custom,
            category: evt.data.category ?? undefined,
            confirmationScope: evt.data.confirmation_scope ?? undefined,
            originatingAgent: evt.data.originating_agent ?? undefined,
            status: "pending",
          });
        } else if (evt.type === "plan") {
          const plan: Plan = {
            planId: evt.data.plan_id,
            title: evt.data.title,
            operations: evt.data.operations,
            riskLevel: evt.data.risk_level as Plan["riskLevel"],
            estimatedCostNote: evt.data.estimated_cost_note,
            criticVerdict: evt.data.critic_verdict,
            revisionCount: evt.data.revision_count,
            status: evt.data.status ?? "pending",
          };
          onMessagesChange((prev) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              plan,
              plans: [...(last.plans ?? []), plan],
            };
            return msgs;
          });
        } else if (evt.type === "reply") {
          onMessagesChange((prev) => {
            const msgs = [...prev];
            msgs[msgs.length - 1] = {
              ...msgs[msgs.length - 1],
              content: evt.data.content,
              isStreaming: false,
            };
            return msgs;
          });
        } else if (evt.type === "usage") {
          onTokenUsage({ input: evt.data.input_tokens, output: evt.data.output_tokens });
        }
      }
    } finally {
      setLoading(false);
    }
  }

  async function handleResumeAfterApproval() {
    setLoading(true);
    onMessagesChange((prev) => [
      ...collapsePreviousAgentRich(prev),
      { role: "agent", content: "", toolCalls: [], isStreaming: true, richCollapsed: false },
    ]);

    try {
      for await (const evt of streamChat("execute approved plan", subscriptionId, sessionId)) {
        if (evt.type === "tool_call") {
          onMessagesChange((prev) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              toolCalls: [...(last.toolCalls ?? []), { tool: evt.data.tool, done: false }],
            };
            return msgs;
          });
        } else if (evt.type === "tool_result") {
          onMessagesChange((prev) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              toolCalls: last.toolCalls?.map((tc) =>
                tc.tool === evt.data.tool && !tc.done
                  ? { ...tc, done: true, success: evt.data.success }
                  : tc
              ),
            };
            return msgs;
          });
        } else if (evt.type === "plan") {
          const plan: Plan = {
            planId: evt.data.plan_id,
            title: evt.data.title,
            operations: evt.data.operations,
            riskLevel: evt.data.risk_level as Plan["riskLevel"],
            estimatedCostNote: evt.data.estimated_cost_note,
            criticVerdict: evt.data.critic_verdict,
            revisionCount: evt.data.revision_count,
            status: evt.data.status ?? "pending",
          };
          onMessagesChange((prev) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              plan,
              plans: [...(last.plans ?? []), plan],
            };
            return msgs;
          });
        } else if (evt.type === "question") {
          appendQuestion({
            questionId: evt.data.question_id,
            title: evt.data.title,
            prompt: evt.data.prompt,
            options: evt.data.options,
            defaultValue: evt.data.default_value ?? undefined,
            allowCustom: evt.data.allow_custom,
            category: evt.data.category ?? undefined,
            confirmationScope: evt.data.confirmation_scope ?? undefined,
            originatingAgent: evt.data.originating_agent ?? undefined,
            status: "pending",
          });
        } else if (evt.type === "activity_start") {
          handleActivityStart(evt.data);
        } else if (evt.type === "activity_end") {
          handleActivityEnd(evt.data);
        } else if (evt.type === "reply") {
          onMessagesChange((prev) => {
            const msgs = [...prev];
            msgs[msgs.length - 1] = {
              ...msgs[msgs.length - 1],
              content: evt.data.content,
              isStreaming: false,
            };
            return msgs;
          });
          onDeploymentComplete();
        } else if (evt.type === "usage") {
          onTokenUsage({ input: evt.data.input_tokens, output: evt.data.output_tokens });
        }
      }
    } finally {
      setLoading(false);
    }
  }

  return (
    <div className="flex h-full min-h-0 flex-col bg-[#161b27]">
      <div className="flex flex-shrink-0 items-center gap-2 border-b border-slate-700 px-4 py-2">
        <Bot size={14} className="text-blue-400" />
        <span className="text-xs font-semibold text-slate-300">InfraMapper Agent</span>
        <span className="text-[10px] text-slate-500">· {formatModelName(ORCHESTRATOR_MODEL)}</span>
        {(tokenUsage.input > 0) && (
          <span className="ml-auto text-[10px] text-slate-600 font-mono">
            {tokenUsage.input.toLocaleString()} / 200k tokens
          </span>
        )}
      </div>

      <div className="min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-3">
        {messages.map((msg, i) =>
          msg.role === "user" ? (
            <div key={i} className="w-full rounded-lg border border-blue-900/70 bg-blue-950/40 px-3 py-2">
              <div className="mb-1 text-[10px] font-semibold uppercase tracking-wide text-blue-300">You</div>
              <div className="text-xs text-slate-100 break-words min-w-0 overflow-hidden">
                {msg.content}
              </div>
            </div>
          ) : (
            <AgentMessage
              key={i}
              msg={msg}
              sessionId={sessionId}
              onApproved={handlePlanApproved}
              onQuestionAnswered={handleQuestionAnswered}
              onRejected={() => {}}
            />
          )
        )}
        <div ref={bottomRef} />
      </div>

      <div className="flex-shrink-0 border-t border-slate-700 px-3 py-3">
        {contextNodes.length > 0 && (
          <div className="flex flex-wrap gap-1.5 mb-2">
            {contextNodes.map((n) => (
              <div
                key={n.id}
                className="flex items-center gap-1 bg-blue-900/50 border border-blue-700/60 rounded px-1.5 py-0.5 text-[10px] text-blue-300 max-w-full"
              >
                <span className="truncate max-w-[140px]" title={n.name}>{n.name}</span>
                <span className="text-blue-600 truncate max-w-[80px]">{n.type.split("/").pop()}</span>
                <button
                  onClick={() => onRemoveContext?.(n.id)}
                  className="text-blue-500 hover:text-blue-200 flex-shrink-0 ml-0.5"
                >
                  <X size={9} />
                </button>
              </div>
            ))}
          </div>
        )}
        <div className="flex gap-2 items-end bg-[#1e293b] rounded-lg px-3 py-2">
          <textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Describe infrastructure or ask a question..."
            rows={1}
            className="flex-1 bg-transparent text-xs text-slate-200 placeholder-slate-500 resize-none outline-none max-h-28"
          />
          <button
            onClick={handleSend}
            disabled={loading || !input.trim()}
            className="text-blue-400 hover:text-blue-300 disabled:text-slate-600 flex-shrink-0 pb-0.5"
          >
            <Send size={14} />
          </button>
        </div>
        <p className="text-[10px] text-slate-600 mt-1.5 text-center">Enter to send · Shift+Enter for newline · Click graph nodes to add context</p>
      </div>
    </div>
  );
}
