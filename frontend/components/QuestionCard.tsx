"use client";

import { useState } from "react";
import { HelpCircle, Loader2, Send } from "lucide-react";
import { answerQuestion } from "@/lib/api";
import type { ClarifyingQuestion } from "@/lib/types";

interface Props {
  question: ClarifyingQuestion;
  sessionId: string;
  onAnswered: (questionId: string, answer: string) => void;
}

export default function QuestionCard({ question, sessionId, onAnswered }: Props) {
  const [customOpen, setCustomOpen] = useState(false);
  const [custom, setCustom] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [answer, setAnswer] = useState(question.answer);

  async function submit(value: string) {
    const trimmed = value.trim();
    if (!trimmed || submitting) return;
    setSubmitting(true);
    try {
      await answerQuestion(question.questionId, sessionId, trimmed);
      setAnswer(trimmed);
      onAnswered(question.questionId, trimmed);
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="mt-2 overflow-hidden rounded-lg border border-amber-800/70 bg-[#0f1117] text-xs">
      <div className="border-b border-amber-900/70 bg-slate-900 px-3 py-2.5">
        <div className="flex items-start gap-2">
          <div className="mt-0.5 flex h-7 w-7 flex-shrink-0 items-center justify-center rounded-md border border-amber-800 bg-amber-950 text-amber-300">
            <HelpCircle size={15} />
          </div>
          <div className="min-w-0 flex-1">
            <div className="text-[10px] font-semibold uppercase tracking-wide text-amber-300">Clarification needed</div>
            <div className="break-words font-semibold leading-snug text-slate-100">{question.title}</div>
            <div className="mt-1 leading-relaxed text-slate-400">{question.prompt}</div>
            {question.defaultValue && (
              <div className="mt-2 inline-flex rounded border border-amber-800 bg-amber-950/50 px-2 py-1 text-[10px] font-medium text-amber-200">
                Recommended: {question.defaultValue}
              </div>
            )}
          </div>
        </div>
      </div>

      <div className="space-y-2 px-3 py-2.5">
        {answer ? (
          <div className="rounded border border-green-800 bg-green-950/40 px-2 py-1.5 text-green-300">
            Answered: {answer}
          </div>
        ) : (
          <>
            {question.options.map((option) => (
              <button
                key={option.value}
                onClick={() => submit(option.value)}
                disabled={submitting}
                className="w-full rounded border border-slate-700 bg-slate-900 px-2 py-2 text-left transition-colors hover:border-amber-700 hover:bg-slate-800 disabled:opacity-60"
              >
                <div className="flex items-center gap-2 text-[11px] font-medium text-slate-100">
                  <span>{option.label}</span>
                  {question.defaultValue === option.value && (
                    <span className="rounded bg-amber-900/70 px-1.5 py-0.5 text-[9px] uppercase tracking-wide text-amber-200">
                      Recommended
                    </span>
                  )}
                </div>
                {option.description && <div className="mt-0.5 text-[10px] text-slate-500">{option.description}</div>}
              </button>
            ))}
            {question.allowCustom && (
              <div>
                {!customOpen ? (
                  <button
                    onClick={() => setCustomOpen(true)}
                    className="w-full rounded border border-dashed border-slate-700 px-2 py-2 text-left text-[11px] text-slate-400 hover:border-amber-700 hover:text-slate-200"
                  >
                    Custom
                  </button>
                ) : (
                  <div className="space-y-2">
                    <textarea
                      value={custom}
                      onChange={(e) => setCustom(e.target.value)}
                      rows={3}
                      className="w-full resize-none rounded border border-slate-700 bg-slate-950 px-2 py-2 text-xs text-slate-200 outline-none focus:border-amber-700"
                      placeholder="Describe the custom answer..."
                    />
                    <button
                      onClick={() => submit(custom)}
                      disabled={submitting || !custom.trim()}
                      className="flex items-center gap-1.5 rounded bg-amber-700 px-3 py-1.5 text-white hover:bg-amber-600 disabled:bg-slate-700"
                    >
                      {submitting ? <Loader2 size={12} className="animate-spin" /> : <Send size={12} />}
                      Submit answer
                    </button>
                  </div>
                )}
              </div>
            )}
          </>
        )}
      </div>
    </div>
  );
}
