"use client";

import { useState, useRef, useEffect, useMemo, useLayoutEffect } from "react";
import {
  Send,
  Bot,
  Sparkles,
  X,
  AtSign,
  Paperclip,
  Activity,
  Terminal,
  MessageSquare,
  ListChecks,
  RotateCcw,
} from "lucide-react";
import { streamChat, resetSession } from "@/lib/api";
import type {
  ChatMessage,
  Plan,
  ResourceNode,
  AgentActivityItem,
  ClarifyingQuestion,
  AgentStreamEvent,
} from "@/lib/types";
import ReactMarkdown from "react-markdown";
import PlanCard from "./PlanCard";
import QuestionCard from "./QuestionCard";
import TerminalStream, { deriveLines } from "./TerminalStream";

interface Props {
  sessionId: string;
  subscriptionId: string;
  messages: ChatMessage[];
  onMessagesChange: (
    msgs: ChatMessage[] | ((prev: ChatMessage[]) => ChatMessage[]),
  ) => void;
  onSessionIdSet: (id: string) => void;
  onDeploymentComplete: () => void;
  tokenUsage: { input: number; output: number };
  onTokenUsage: (u: { input: number; output: number }) => void;
  contextNodes?: ResourceNode[];
  onRemoveContext?: (id: string) => void;
  syntheticPrompt?: string | null;
  onSyntheticPromptConsumed?: () => void;
  onResetChat?: () => void;
}

const CLOUDOPS_AGENT_MODEL = "gpt-4.1-mini";
const USER_INITIALS = "LB";
const DIFF_JSON_PATTERN =
  /(Infrastructure JSON:\s*```(?:json)?[\s\S]*?```|```json[\s\S]*?```)/gi;
const DIFF_JSON_DETECT_PATTERN =
  /(Infrastructure JSON:\s*```(?:json)?[\s\S]*?```|```json[\s\S]*?```)/i;

type Tab = "chat" | "plan" | "logs";

function formatModelName(model?: string) {
  return model?.replace(/^gpt-/, "gpt ").replace(/-/g, " ");
}

function collapsePreviousAgentRich(messages: ChatMessage[]) {
  return messages.map((msg) =>
    msg.role === "agent" &&
    ((msg.activities?.length ?? 0) > 0 ||
      (msg.plans?.length ?? 0) > 0 ||
      msg.plan)
      ? { ...msg, richCollapsed: true }
      : msg,
  );
}

function cleanAgentText(value?: string) {
  return value
    ?.replace(/\*\*/g, "")
    .replace(/[\p{Emoji_Presentation}\p{Extended_Pictographic}️]/gu, "")
    .replace(/\s+/g, " ")
    .trim();
}

function formatWorkedDuration(ms: number) {
  const minutes = Math.max(1, Math.ceil(ms / 60000));
  return `${minutes}m`;
}

function approvedPlanPrompt(plan: Plan) {
  return [
    "Execute approved plan. The user clicked Approve in the UI.",
    "Use infra-builder-agent and execute the operations in chronological order.",
    "For delete operations, chronological order means dependents first and parents or referenced resources last.",
    "For create/update/deploy operations, generate the exact resource-level ARM template JSON required for that operation and pass it to the matching tool.",
    "After each operation, call the matching verification tool.",
    "Do not execute anything outside this approved plan.",
    "",
    "Approved plan JSON:",
    "```json",
    JSON.stringify(plan, null, 2),
    "```",
  ].join("\n");
}

function getWorkedDuration(msg: ChatMessage) {
  if (msg.isStreaming || !msg.activities || msg.activities.length === 0)
    return null;
  const startedAt = Math.min(
    ...msg.activities.map((item) => item.startedAt ?? item.endedAt ?? Infinity),
  );
  const endedAt = Math.max(
    ...msg.activities.map(
      (item) => item.endedAt ?? item.startedAt ?? -Infinity,
    ),
  );
  if (
    !Number.isFinite(startedAt) ||
    !Number.isFinite(endedAt) ||
    endedAt < startedAt
  )
    return null;
  return formatWorkedDuration(endedAt - startedAt);
}

function waitForActivityPaint() {
  return new Promise<number>((resolve: (time: number) => void) => {
    requestAnimationFrame((time) => {
      resolve(time);
    });
  });
}

function DiffJsonNugget() {
  return (
    <span className="inline-flex items-center rounded-md border border-cyan-400/40 bg-cyan-500/15 px-2 py-0.5 font-mono text-[9px] font-semibold uppercase tracking-wide text-cyan-200">
      Diff JSON
    </span>
  );
}

function ResourceContextNugget({ node }: { node: ResourceNode }) {
  return (
    <span
      title={`${node.name} (${node.type})`}
      className="inline-flex max-w-[180px] items-center gap-1 rounded-md border border-cyan-400/30 bg-cyan-500/10 px-2 py-0.5 text-[9px] font-medium text-cyan-100"
    >
      <span className="truncate">{node.name}</span>
      <span className="shrink-0 text-cyan-500">
        {node.type.split("/").pop()}
      </span>
    </span>
  );
}

function formatUserContent(content: string) {
  return content
    .replace(DIFF_JSON_PATTERN, "")
    .replace(/\s*Infrastructure JSON:\s*$/i, "")
    .trim();
}

function UserBubble({
  content,
  contextNodes = [],
}: {
  content: string;
  contextNodes?: ResourceNode[];
}) {
  const hasDiffJson = DIFF_JSON_DETECT_PATTERN.test(content);
  const visibleContent = formatUserContent(content);
  const hasContext = hasDiffJson || contextNodes.length > 0;

  return (
    <div className="space-y-1.5">
      <div className="flex items-center gap-2">
        <span className="inline-flex h-5 w-5 items-center justify-center rounded bg-gradient-to-br from-cyan-400 to-cyan-600 text-[9px] font-bold text-slate-900">
          {USER_INITIALS}
        </span>
        <span className="text-[10px] font-semibold uppercase tracking-wider text-slate-400">
          You
        </span>
      </div>
      <div className="rounded-lg border border-slate-800 bg-[#1a2234] px-3 py-2 text-xs leading-relaxed text-slate-200 break-words">
        <span className="whitespace-pre-wrap">{visibleContent}</span>
        {hasContext && (
          <div className="mt-2 flex flex-wrap items-center gap-1.5 border-t border-slate-700/60 pt-2 text-[10px] text-slate-500">
            <span>Context:</span>
            {hasDiffJson && <DiffJsonNugget />}
            {contextNodes.map((node) => (
              <ResourceContextNugget key={node.id} node={node} />
            ))}
          </div>
        )}
      </div>
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
  onApproved: (planId: string) => void;
  onRejected: (planId: string) => void;
  onQuestionAnswered: (questionId: string, answer: string) => void;
}) {
  const workedDuration = getWorkedDuration(msg);
  const hasPlans = (msg.plans?.length ?? 0) > 0 || !!msg.plan;
  const hasQuestions = (msg.questions?.length ?? 0) > 0 || !!msg.question;
  const activities =
    msg.activities && msg.activities.length > 0
      ? msg.activities
      : msg.isStreaming
        ? [
            {
              id: "stream-starting",
              kind: "agent" as const,
              agent: "infra-reader-agent",
              model: CLOUDOPS_AGENT_MODEL,
              status: "running" as const,
              summary: "Invoking infra-reader-agent",
              message: "Invoking infra-reader-agent...",
            },
          ]
        : undefined;

  return (
    <div className="space-y-2">
      <div className="flex items-center gap-2 text-[10px]">
        <span className="text-cyan-400">+</span>
        <span className="font-semibold uppercase tracking-wider text-slate-300">
          InfraMapper
        </span>
        <span className="text-slate-500">
          ◇ {formatModelName(CLOUDOPS_AGENT_MODEL)}
        </span>
        {workedDuration && (
          <span className="ml-auto text-slate-500">{workedDuration}</span>
        )}
      </div>

      <div className="border-l-2 border-slate-800 pl-3">
        <TerminalStream activities={activities} />

        {(msg.plans ?? (msg.plan ? [msg.plan] : [])).map((plan) => (
          <PlanCard
            key={plan.planId}
            plan={plan}
            sessionId={sessionId}
            onApproved={onApproved}
            onRejected={onRejected}
          />
        ))}

        {(msg.questions ?? (msg.question ? [msg.question] : [])).map(
          (question) => (
            <QuestionCard
              key={question.questionId}
              question={question}
              sessionId={sessionId}
              onAnswered={onQuestionAnswered}
            />
          ),
        )}

        {msg.content && !hasPlans && !hasQuestions && (
          <div className="mt-2 text-xs leading-relaxed text-slate-200">
            <ReactMarkdown
              components={{
                p: ({ children }) => (
                  <p className="mb-1 last:mb-0">{children}</p>
                ),
                ul: ({ children }) => (
                  <ul className="mb-1 space-y-0.5 list-disc pl-4">
                    {children}
                  </ul>
                ),
                ol: ({ children }) => (
                  <ol className="list-decimal pl-4 mb-1">{children}</ol>
                ),
                li: ({ children }) => <li className="mb-0.5">{children}</li>,
                strong: ({ children }) => (
                  <strong className="text-white font-semibold">
                    {children}
                  </strong>
                ),
                code: ({ children }) => (
                  <code className="bg-slate-900 px-1 rounded text-[10px] text-cyan-300 break-all">
                    {children}
                  </code>
                ),
                pre: ({ children }) => (
                  <pre className="bg-slate-900 rounded p-2 text-[10px] text-cyan-300 overflow-x-auto whitespace-pre-wrap break-all my-1">
                    {children}
                  </pre>
                ),
              }}
            >
              {cleanAgentText(msg.content)}
            </ReactMarkdown>
          </div>
        )}
      </div>
    </div>
  );
}

function TabBar({
  active,
  onChange,
}: {
  active: Tab;
  onChange: (t: Tab) => void;
}) {
  const tabs: { id: Tab; label: string; icon: React.ReactNode }[] = [
    { id: "chat", label: "Chat", icon: <MessageSquare size={11} /> },
    { id: "plan", label: "Plan", icon: <ListChecks size={11} /> },
    { id: "logs", label: "Logs", icon: <Terminal size={11} /> },
  ];
  return (
    <div className="flex items-center gap-1 px-2 py-1.5">
      {tabs.map((t) => (
        <button
          key={t.id}
          onClick={() => onChange(t.id)}
          className={`inline-flex items-center gap-1.5 rounded px-2.5 py-1 text-[11px] font-medium transition-colors ${
            active === t.id
              ? "bg-slate-800 text-cyan-300"
              : "text-slate-500 hover:text-slate-300"
          }`}
        >
          {t.icon}
          {t.label}
        </button>
      ))}
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
  onResetChat,
}: Props) {
  const [input, setInput] = useState("");
  const [loading, setLoading] = useState(false);
  const [activeTab, setActiveTab] = useState<Tab>("chat");
  const bottomRef = useRef<HTMLDivElement>(null);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  function updateLatestAgentMessage(
    patchMessage: (msg: ChatMessage) => ChatMessage,
  ) {
    onMessagesChange((prev: ChatMessage[]) => {
      const msgs = [...prev];
      let index = -1;
      for (let i = msgs.length - 1; i >= 0; i--) {
        if (msgs[i].role === "agent" && (msgs[i].isStreaming || index < 0)) {
          index = i;
          if (msgs[i].isStreaming) break;
        }
      }
      if (index < 0) {
        msgs.push({
          role: "agent",
          content: "",
          toolCalls: [],
          isStreaming: true,
          richCollapsed: false,
        });
        index = msgs.length - 1;
      }
      msgs[index] = patchMessage(msgs[index]);
      return msgs;
    });
  }

  function upsertActivity(item: AgentActivityItem) {
    updateLatestAgentMessage((msg) => {
      const activities = msg.activities ?? [];
      const existing = activities.find((p) => p.id === item.id);
      return {
        ...msg,
        activities: existing
          ? activities.map((p) =>
              p.id === item.id
                ? { ...p, ...item, startedAt: p.startedAt ?? item.startedAt }
                : p,
            )
          : [...activities, item],
      };
    });
  }

  function handleActivityStart(
    data: Extract<AgentStreamEvent, { type: "activity_start" }>["data"],
  ) {
    upsertActivity({
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
    });
  }

  function handleActivityEnd(
    data: Extract<AgentStreamEvent, { type: "activity_end" }>["data"],
  ) {
    updateLatestAgentMessage((msg) => {
      const activities = msg.activities ?? [];
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
      return {
        ...msg,
        activities: existing
          ? activities.map((p) => (p.id === data.id ? { ...p, ...patch } : p))
          : [...activities, fallback],
      };
    });
  }

  function finishLatestAgentMessage(
    content: string,
    status: AgentActivityItem["status"] = "success",
  ) {
    updateLatestAgentMessage((msg) => {
      const activities =
        msg.activities && msg.activities.length > 0
          ? msg.activities
          : [
              {
                id: "agent-response",
                kind: "agent" as const,
                agent: "infra-reader-agent",
                model: CLOUDOPS_AGENT_MODEL,
                status,
                summary:
                  status === "failed"
                    ? "Agent response failed"
                    : "Agent response completed",
                message: status === "failed" ? content : "Response received",
                endedAt: Date.now(),
              },
            ];
      return { ...msg, content, activities, isStreaming: false };
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

  function normalizeQuestionOptions(options: ClarifyingQuestion["options"]) {
    return options.map((option) => {
      const legacy = option as typeof option & {
        Label?: string;
        Value?: string;
        Description?: string;
      };
      return {
        label:
          option.label ??
          legacy.Label ??
          option.value ??
          legacy.Value ??
          "Option",
        value:
          option.value ??
          legacy.Value ??
          option.label ??
          legacy.Label ??
          "option",
        description: option.description ?? legacy.Description,
      };
    });
  }

  useLayoutEffect(() => {
    if (activeTab !== "chat") return;
    bottomRef.current?.scrollIntoView({ behavior: "auto", block: "end" });
  }, [activeTab]);

  useEffect(() => {
    if (activeTab !== "chat") return;
    bottomRef.current?.scrollIntoView({ behavior: "smooth", block: "end" });
  }, [messages, activeTab]);

  useEffect(() => {
    if (syntheticPrompt && !loading) {
      onSyntheticPromptConsumed?.();
      handleSendText(syntheticPrompt);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [syntheticPrompt]);

  useEffect(() => {
    const ta = textareaRef.current;
    if (!ta) return;
    ta.style.height = "auto";
    ta.style.height = `${Math.min(ta.scrollHeight, 200)}px`;
  }, [input]);

  async function handleSendText(overrideText: string) {
    const text = overrideText.trim();
    if (!text || loading) return;

    const contextBlock =
      contextNodes.length > 0
        ? `[Selected resources for context:\n${contextNodes
            .map(
              (n) =>
                `- ${n.name} (${n.type}, group: ${n.resourceGroup}, location: ${n.location}, id: ${n.id})`,
            )
            .join("\n")}]\n\n`
        : "";
    const fullText = contextBlock + text;

    setInput("");
    contextNodes.forEach((n) => onRemoveContext?.(n.id));
    const newMessages: ChatMessage[] = [
      ...collapsePreviousAgentRich(messages),
      { role: "user", content: text, contextNodes: [...contextNodes] },
      {
        role: "agent",
        content: "",
        toolCalls: [],
        isStreaming: true,
        richCollapsed: false,
      },
    ];
    onMessagesChange(newMessages);
    setLoading(true);
    setActiveTab("chat");

    try {
      for await (const evt of streamChat(fullText, subscriptionId, sessionId)) {
        if (evt.type === "tool_call") {
          onSessionIdSet(evt.data.session_id);
          onMessagesChange((prev: ChatMessage[]) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              toolCalls: [
                ...(last.toolCalls ?? []),
                { tool: evt.data.tool, done: false },
              ],
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
                  : tc,
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
            resources: evt.data.resources,
            dependencies: evt.data.dependencies,
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
              plans: [
                ...(last.plans ?? []).filter((p) => p.planId !== plan.planId),
                plan,
              ],
            };
            return msgs;
          });
        } else if (evt.type === "question") {
          onSessionIdSet(evt.data.session_id);
          appendQuestion({
            questionId: evt.data.question_id,
            title: evt.data.title,
            prompt: evt.data.prompt,
            options: normalizeQuestionOptions(evt.data.options),
            defaultValue: evt.data.default_value ?? undefined,
            allowCustom: evt.data.allow_custom,
            category: evt.data.category ?? undefined,
            confirmationScope: evt.data.confirmation_scope ?? undefined,
            originatingAgent: evt.data.originating_agent ?? undefined,
            status: "pending",
          });
        } else if (evt.type === "activity_start") {
          handleActivityStart(evt.data);
          await waitForActivityPaint();
        } else if (evt.type === "activity_end") {
          handleActivityEnd(evt.data);
        } else if (evt.type === "usage") {
          onTokenUsage({
            input: evt.data.input_tokens,
            output: evt.data.output_tokens,
          });
        } else if (evt.type === "reply") {
          onSessionIdSet(evt.data.session_id);
          finishLatestAgentMessage(evt.data.content);
          const lower = evt.data.content.toLowerCase();
          if (
            lower.includes("succeeded") ||
            lower.includes("deployed") ||
            lower.includes("created")
          ) {
            onDeploymentComplete();
          }
        } else if (evt.type === "error") {
          finishLatestAgentMessage(`Error: ${evt.data.message}`, "failed");
        }
      }
    } catch (err) {
      finishLatestAgentMessage(`Error: ${(err as Error).message}`, "failed");
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

  function markPlanStatus(planId: string, next: Plan["status"]) {
    onMessagesChange((prev) =>
      prev.map((m) => {
        const single =
          m.plan?.planId === planId ? { ...m.plan, status: next } : m.plan;
        const list = m.plans?.map((p) =>
          p.planId === planId ? { ...p, status: next } : p,
        );
        return { ...m, plan: single, plans: list };
      }),
    );
  }

  function findPlan(planId: string) {
    for (let i = messages.length - 1; i >= 0; i--) {
      const msg = messages[i];
      const plans = msg.plans ?? (msg.plan ? [msg.plan] : []);
      const match = plans.find((plan) => plan.planId === planId);
      if (match) return match;
    }
    return null;
  }

  async function executeApprovedPlan(plan: Plan) {
    if (loading) return;

    setLoading(true);
    setActiveTab("chat");
    onMessagesChange((prev) => [
      ...collapsePreviousAgentRich(prev),
      {
        role: "agent",
        content: "",
        toolCalls: [],
        isStreaming: true,
        richCollapsed: false,
      },
    ]);

    try {
      for await (const evt of streamChat(
        approvedPlanPrompt(plan),
        subscriptionId,
        sessionId,
      )) {
        if (evt.type === "tool_call") {
          onSessionIdSet(evt.data.session_id);
          onMessagesChange((prev: ChatMessage[]) => {
            const msgs = [...prev];
            const last = msgs[msgs.length - 1];
            msgs[msgs.length - 1] = {
              ...last,
              toolCalls: [
                ...(last.toolCalls ?? []),
                { tool: evt.data.tool, done: false },
              ],
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
                  : tc,
              ),
            };
            return msgs;
          });
        } else if (evt.type === "activity_start") {
          handleActivityStart(evt.data);
          await waitForActivityPaint();
        } else if (evt.type === "activity_end") {
          handleActivityEnd(evt.data);
        } else if (evt.type === "usage") {
          onTokenUsage({
            input: evt.data.input_tokens,
            output: evt.data.output_tokens,
          });
        } else if (evt.type === "reply") {
          onSessionIdSet(evt.data.session_id);
          finishLatestAgentMessage(evt.data.content);
          const lower = evt.data.content.toLowerCase();
          const verificationMismatch = lower.includes(
            "verification_status: mismatch",
          );
          const verificationSuccess = lower.includes(
            "verification_status: success",
          );
          if (
            !verificationMismatch &&
            (verificationSuccess ||
              lower.includes("succeeded") ||
              lower.includes("deployed") ||
              lower.includes("created") ||
              lower.includes("deleted") ||
              lower.includes("completed"))
          ) {
            markPlanStatus(plan.planId, "completed");
            onDeploymentComplete();
          }
        } else if (evt.type === "error") {
          finishLatestAgentMessage(`Error: ${evt.data.message}`, "failed");
        }
      }
    } catch (err) {
      finishLatestAgentMessage(`Error: ${(err as Error).message}`, "failed");
    } finally {
      setLoading(false);
    }
  }

  function handlePlanApproved(planId?: string) {
    if (planId) {
      markPlanStatus(planId, "approved");
      const plan = findPlan(planId);
      if (plan) {
        void executeApprovedPlan({ ...plan, status: "approved" });
      }
    }
  }

  function handlePlanRejected(planId?: string) {
    if (planId) markPlanStatus(planId, "rejected");
  }

  function handleQuestionAnswered(questionId: string, answer: string) {
    const next = messages.map((msg) => {
      const questions = msg.questions?.map((q) =>
        q.questionId === questionId
          ? { ...q, status: "answered" as const, answer }
          : q,
      );
      return {
        ...msg,
        question:
          msg.question?.questionId === questionId
            ? { ...msg.question, status: "answered" as const, answer }
            : msg.question,
        questions,
      };
    });
    onMessagesChange(next);
    const pending = next
      .flatMap((m) => m.questions ?? (m.question ? [m.question] : []))
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
      {
        role: "agent",
        content: "",
        toolCalls: [],
        isStreaming: true,
        richCollapsed: false,
      },
    ]);
    try {
      for await (const evt of streamChat(
        "continue with clarification answer",
        subscriptionId,
        sessionId,
      )) {
        if (evt.type === "activity_start") {
          handleActivityStart(evt.data);
          await waitForActivityPaint();
        } else if (evt.type === "activity_end") handleActivityEnd(evt.data);
        else if (evt.type === "question") {
          appendQuestion({
            questionId: evt.data.question_id,
            title: evt.data.title,
            prompt: evt.data.prompt,
            options: normalizeQuestionOptions(evt.data.options),
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
            resources: evt.data.resources,
            dependencies: evt.data.dependencies,
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
              plans: [
                ...(last.plans ?? []).filter((p) => p.planId !== plan.planId),
                plan,
              ],
            };
            return msgs;
          });
        } else if (evt.type === "reply")
          finishLatestAgentMessage(evt.data.content);
        else if (evt.type === "usage")
          onTokenUsage({
            input: evt.data.input_tokens,
            output: evt.data.output_tokens,
          });
      }
    } finally {
      setLoading(false);
    }
  }

  const latestPlan = useMemo(() => {
    for (let i = messages.length - 1; i >= 0; i--) {
      const m = messages[i];
      const plans = m.plans ?? (m.plan ? [m.plan] : []);
      if (plans.length > 0) return plans[plans.length - 1];
    }
    return null;
  }, [messages]);

  const allActivities = useMemo(() => {
    const out: AgentActivityItem[] = [];
    const seen = new Set<string>();
    for (const m of messages) {
      if (!m.activities) continue;
      for (const a of m.activities) {
        if (seen.has(a.id)) continue;
        seen.add(a.id);
        out.push(a);
      }
    }
    return out;
  }, [messages]);

  return (
    <div className="flex h-full min-h-0 flex-col bg-[#0b1018]">
      <div className="flex flex-shrink-0 items-center justify-between border-b border-slate-800 bg-[#0f1421]">
        <div className="flex items-center gap-2 px-3 py-2">
          <div className="flex h-6 w-6 items-center justify-center rounded bg-gradient-to-br from-cyan-400/30 to-cyan-600/30 border border-cyan-500/40">
            <Bot size={12} className="text-cyan-300" />
          </div>
          <span className="text-xs font-semibold text-slate-200">
            InfraMapper
          </span>
          <button className="inline-flex items-center gap-1 rounded border border-slate-700 bg-slate-900 px-2 py-0.5 font-mono text-[10px] text-slate-300 hover:bg-slate-800">
            {formatModelName(CLOUDOPS_AGENT_MODEL)}
          </button>
        </div>
        <div className="flex items-center gap-3 px-3">
          {tokenUsage.input > 0 && (
            <span className="text-[10px] text-slate-600 font-mono">
              {tokenUsage.input.toLocaleString()} / 200k
            </span>
          )}
          <button
            onClick={async () => {
              if (loading) return;
              if (sessionId) {
                try {
                  await resetSession(sessionId);
                } catch {
                  /* ignore; clear UI anyway */
                }
              }
              onResetChat?.();
            }}
            disabled={loading}
            title="Restart chat (clears all memory)"
            className="inline-flex items-center gap-1 rounded border border-slate-700 px-2 py-1 text-[10px] text-slate-400 hover:border-cyan-500/60 hover:text-cyan-300 disabled:opacity-40"
          >
            <RotateCcw size={11} />
            Restart
          </button>
        </div>
      </div>

      <div className="flex-shrink-0 border-b border-slate-800">
        <TabBar active={activeTab} onChange={setActiveTab} />
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        {activeTab === "chat" && (
          <div className="space-y-5 px-4 py-4">
            {messages.length === 0 && (
              <div className="text-center text-[11px] text-slate-600 py-12">
                <Sparkles size={16} className="mx-auto mb-2 text-cyan-500/60" />
                Describe infrastructure or ask a question.
              </div>
            )}
            {messages.map((msg, i) =>
              msg.role === "user" ? (
                <UserBubble
                  key={i}
                  content={msg.content}
                  contextNodes={msg.contextNodes}
                />
              ) : (
                <AgentMessage
                  key={i}
                  msg={msg}
                  sessionId={sessionId}
                  onApproved={handlePlanApproved}
                  onQuestionAnswered={handleQuestionAnswered}
                  onRejected={handlePlanRejected}
                />
              ),
            )}
            <div ref={bottomRef} />
          </div>
        )}

        {activeTab === "plan" && (
          <div className="px-4 py-4">
            {latestPlan ? (
              <PlanCard
                plan={latestPlan}
                sessionId={sessionId}
                onApproved={handlePlanApproved}
                onRejected={handlePlanRejected}
              />
            ) : (
              <div className="text-center text-[11px] text-slate-600 py-12">
                <ListChecks size={16} className="mx-auto mb-2 text-slate-700" />
                No plan yet. Ask the agent to create infrastructure.
              </div>
            )}
          </div>
        )}

        {activeTab === "logs" && (
          <div className="px-4 py-4">
            <div className="mb-2 flex items-center gap-2 text-[10px] uppercase tracking-wider text-slate-500">
              <Terminal size={11} />
              Agent log · session {sessionId ? sessionId.slice(0, 8) : "new"}
            </div>
            {deriveLines(allActivities).length > 0 ? (
              <TerminalStream activities={allActivities} />
            ) : (
              <div className="text-[11px] text-slate-600 italic">
                No events yet.
              </div>
            )}
          </div>
        )}
      </div>

      <div className="flex-shrink-0 border-t border-slate-800 px-3 py-3">
        <div className="mb-1.5 flex items-center gap-2 text-[10px] text-slate-500">
          <span>Context:</span>
          {contextNodes.length === 0 ? (
            <span className="rounded border border-slate-800 px-1.5 py-0.5 text-slate-600">
              none selected
            </span>
          ) : (
            <div className="flex flex-wrap gap-1">
              {contextNodes.map((n) => (
                <div
                  key={n.id}
                  className="inline-flex items-center gap-1 rounded border border-cyan-700/50 bg-cyan-950/40 px-1.5 py-0.5 text-[10px] text-cyan-300"
                >
                  <span className="truncate max-w-[140px]" title={n.name}>
                    {n.name}
                  </span>
                  <span className="text-cyan-700">
                    {n.type.split("/").pop()}
                  </span>
                  <button
                    onClick={() => onRemoveContext?.(n.id)}
                    className="text-cyan-600 hover:text-cyan-200"
                  >
                    <X size={9} />
                  </button>
                </div>
              ))}
            </div>
          )}
        </div>

        <div className="rounded-lg border border-slate-700 bg-[#0f172a] px-3 py-2 focus-within:border-cyan-500/60 focus-within:ring-1 focus-within:ring-cyan-500/40 transition-colors">
          <textarea
            ref={textareaRef}
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Describe infrastructure or ask a question..."
            rows={2}
            className="block w-full resize-none bg-transparent text-xs text-slate-200 placeholder-slate-600 outline-none"
          />
          <div className="mt-2 flex items-center justify-between">
            <div className="flex items-center gap-2 text-slate-500">
              <button
                title="Attach"
                className="rounded p-1 hover:bg-slate-800 hover:text-slate-300"
              >
                <Paperclip size={12} />
              </button>
              <button
                title="Mention resource"
                className="rounded p-1 hover:bg-slate-800 hover:text-slate-300"
              >
                <AtSign size={12} />
              </button>
              <button
                title="Activity"
                className="rounded p-1 hover:bg-slate-800 hover:text-slate-300"
              >
                <Activity size={12} />
              </button>
            </div>
            <button
              onClick={handleSend}
              disabled={loading || !input.trim()}
              className="inline-flex items-center gap-1.5 rounded bg-cyan-500 px-3 py-1.5 text-[11px] font-semibold text-slate-900 hover:bg-cyan-400 disabled:bg-slate-700 disabled:text-slate-500"
            >
              <Send size={11} />
              Send
            </button>
          </div>
        </div>
        <p className="mt-1.5 text-center text-[10px] text-slate-600">
          Enter to send · Shift+Enter for newline · Click graph nodes to add
          context
        </p>
      </div>
    </div>
  );
}
