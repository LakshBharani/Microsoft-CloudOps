"use client";

import { useState, useRef, useEffect, useCallback } from "react";
import { Send, Bot, User, Wrench, CheckCircle2, XCircle, X } from "lucide-react";
import { streamChat } from "@/lib/api";
import type { ChatMessage, ToolCall, Plan, ResourceNode } from "@/lib/types";
import ReactMarkdown from "react-markdown";
import PlanCard from "./PlanCard";

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
};

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

function AgentMessage({
  msg,
  sessionId,
  onApproved,
  onRejected,
}: {
  msg: ChatMessage;
  sessionId: string;
  onApproved: () => void;
  onRejected: () => void;
}) {
  return (
    <div className="flex gap-2 justify-start">
      <div className="w-6 h-6 rounded-full bg-blue-600 flex items-center justify-center flex-shrink-0 mt-0.5">
        <Bot size={12} className="text-white" />
      </div>
      <div className="max-w-[85%] bg-[#1e293b] rounded-lg px-3 py-2 text-xs leading-relaxed text-slate-200 break-words min-w-0 overflow-hidden">
        {msg.toolCalls && msg.toolCalls.length > 0 && (
          <ToolCallList toolCalls={msg.toolCalls} />
        )}
        {msg.plan && (
          <PlanCard
            plan={msg.plan}
            sessionId={sessionId}
            onApproved={onApproved}
            onRejected={onRejected}
          />
        )}
        {msg.content && (
          <ReactMarkdown
            components={{
              p: ({ children }) => <p className="mb-1 last:mb-0">{children}</p>,
              ul: ({ children }) => <ul className="list-disc pl-4 mb-1">{children}</ul>,
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
            {msg.content}
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

  const updateLastMessage = useCallback((updater: (msg: ChatMessage) => ChatMessage) => {
    onMessagesChange(
      (() => {
        const msgs = [...messages];
        if (msgs.length > 0) msgs[msgs.length - 1] = updater(msgs[msgs.length - 1]);
        return msgs;
      })()
    );
  }, [messages, onMessagesChange]);

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
      ...messages,
      { role: "user", content: text },
      { role: "agent", content: "", toolCalls: [], isStreaming: true },
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
            status: "pending",
          };
          onMessagesChange((prev: ChatMessage[]) => {
            const msgs = [...prev];
            msgs[msgs.length - 1] = { ...msgs[msgs.length - 1], plan };
            return msgs;
          });
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
    // After approval the backend auto-injects a resume message;
    // trigger a follow-up chat to continue the agent
    setTimeout(() => {
      const resumeMessages: ChatMessage[] = [
        ...messages.map((m) => m),
        { role: "agent", content: "", toolCalls: [], isStreaming: true },
      ];
      // We need to call the stream with the continuation message
      // The backend already injected "plan approved" into history, so we send empty resume
      handleResumeAfterApproval();
    }, 300);
  }

  async function handleResumeAfterApproval() {
    setLoading(true);
    onMessagesChange((prev) => [
      ...prev,
      { role: "agent", content: "", toolCalls: [], isStreaming: true },
    ]);

    try {
      for await (const evt of streamChat("continue", subscriptionId, sessionId)) {
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
    <div className="flex flex-col h-full bg-[#161b27]">
      <div className="px-4 py-2 border-b border-slate-700 flex items-center gap-2">
        <Bot size={14} className="text-blue-400" />
        <span className="text-xs font-semibold text-slate-300">InfraMapper Agent</span>
        {(tokenUsage.input > 0) && (
          <span className="ml-auto text-[10px] text-slate-600 font-mono">
            {tokenUsage.input.toLocaleString()} / 200k tokens
          </span>
        )}
      </div>

      <div className="flex-1 overflow-y-auto px-4 py-3 space-y-3">
        {messages.map((msg, i) =>
          msg.role === "user" ? (
            <div key={i} className="flex gap-2 justify-end">
              <div className="max-w-[85%] bg-blue-600 text-white rounded-lg px-3 py-2 text-xs break-words min-w-0 overflow-hidden">
                {msg.content}
              </div>
              <div className="w-6 h-6 rounded-full bg-slate-600 flex items-center justify-center flex-shrink-0 mt-0.5">
                <User size={12} className="text-white" />
              </div>
            </div>
          ) : (
            <AgentMessage
              key={i}
              msg={msg}
              sessionId={sessionId}
              onApproved={handlePlanApproved}
              onRejected={() => {}}
            />
          )
        )}
        <div ref={bottomRef} />
      </div>

      <div className="px-3 py-3 border-t border-slate-700">
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
