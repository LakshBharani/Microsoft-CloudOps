export interface ResourceNode {
  id: string;
  name: string;
  type: string;
  location: string;
  resourceGroup: string;
  tags?: Record<string, string>;
  properties?: Record<string, unknown>;
  skuJson?: string | null;
  kind?: string | null;
}

export interface DependencyEdge {
  sourceId: string;
  targetId: string;
  dependencyType: string;
  riskWeight?: number;
}

export interface InfrastructureGraph {
  nodes: ResourceNode[];
  edges: DependencyEdge[];
}

export interface ToolCall {
  tool: string;
  done: boolean;
  success?: boolean;
}

export interface AgentActivityItem {
  id: string;
  parentId?: string;
  kind: "group_chat" | "agent" | "tool" | "plan" | "question" | "error";
  agent?: string;
  tool?: string;
  model?: string;
  status: "running" | "success" | "failed" | "rejected" | "pending";
  summary: string;
  detailPreview?: string;
  errorType?: string;
  message?: string;
  startedAt?: number;
  endedAt?: number;
}

export interface ClarifyingQuestionOption {
  label: string;
  value: string;
  description?: string;
}

export interface ClarifyingQuestion {
  questionId: string;
  title: string;
  prompt: string;
  options: ClarifyingQuestionOption[];
  defaultValue?: string;
  allowCustom: boolean;
  category?: string;
  confirmationScope?: string;
  originatingAgent?: string;
  status?: "pending" | "answered";
  answer?: string;
}

export interface PlanOperation {
  action: "Create" | "Update" | "Delete" | "Deploy" | string;
  resource_type: string;
  resource_name: string;
  resource_group?: string;
  details?: string | Record<string, unknown>;
}

export interface PlanResource {
  action?: "Create" | "Update" | "Delete" | "Deploy" | string;
  resource_type?: string;
  resource_name: string;
  resource_group?: string;
  note?: string;
}

export interface Plan {
  planId: string;
  title: string;
  operations: PlanOperation[];
  resources?: PlanResource[];
  dependencies?: string;
  riskLevel: "Low" | "Medium" | "High";
  estimatedCostNote?: string;
  revisionCount?: number;
  criticVerdict?: string;
  status?: "pending" | "approved" | "rejected" | "completed";
}

export interface ChatMessage {
  role: "user" | "agent";
  content: string;
  contextNodes?: ResourceNode[];
  toolCalls?: ToolCall[];
  plan?: Plan;
  plans?: Plan[];
  question?: ClarifyingQuestion;
  questions?: ClarifyingQuestion[];
  activities?: AgentActivityItem[];
  richCollapsed?: boolean;
  isStreaming?: boolean;
}

export interface Session {
  id: string;
  name: string;
  createdAt: number;
}

export interface DesiredResourceNode {
  name: string;
  type: string;
  resourceGroup: string;
  location: string;
  skuJson?: string;
  kind?: string;
  tags?: Record<string, string>;
}

export interface DesiredStateSpec {
  nodes: DesiredResourceNode[];
  edges: { sourceName: string; targetName: string; dependencyType: string }[];
  scope?: string[];
}

export interface DiffChange {
  field: string;
  from?: string;
  to?: string;
}

export interface DiffNode {
  name: string;
  type: string;
  resourceGroup: string;
  location: string;
  existingId?: string;
  changes: DiffChange[];
}

export interface DiffResult {
  toCreate: DiffNode[];
  toUpdate: DiffNode[];
  toDelete: DiffNode[];
  unchanged: DiffNode[];
}

export interface AgentChatResponse {
  reply: string;
  sessionId: string;
}

export type AgentStreamEvent =
  | { type: "tool_call"; data: { tool: string; session_id: string } }
  | { type: "tool_result"; data: { tool: string; success: boolean; session_id: string } }
  | { type: "plan"; data: { plan_id: string; title: string; operations: PlanOperation[]; resources?: PlanResource[]; dependencies?: string; risk_level: string; estimated_cost_note?: string; critic_verdict?: string; revision_count?: number; status?: Plan["status"]; session_id: string } }
  | { type: "reply"; data: { content: string; session_id: string } }
  | { type: "usage"; data: { input_tokens: number; output_tokens: number; session_id: string } }
  | { type: "error"; data: { message: string; session_id: string } }
  | { type: "activity_start"; data: { id: string; parent_id?: string | null; kind: AgentActivityItem["kind"]; agent?: string | null; tool?: string | null; model?: string | null; status: AgentActivityItem["status"]; summary: string; detail_preview?: string | null; error_type?: string | null; message?: string | null; session_id: string } }
  | { type: "activity_end"; data: { id: string; parent_id?: string | null; kind?: AgentActivityItem["kind"]; agent?: string | null; tool?: string | null; model?: string | null; status: AgentActivityItem["status"]; summary: string; detail_preview?: string | null; error_type?: string | null; message?: string | null; session_id: string } }
  | { type: "question"; data: { question_id: string; title: string; prompt: string; options: ClarifyingQuestionOption[]; default_value?: string | null; allow_custom: boolean; category?: string | null; confirmation_scope?: string | null; originating_agent?: string | null; session_id: string } };
