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

export interface PlanOperation {
  action: "Create" | "Update" | "Delete" | "Deploy";
  resource_type: string;
  resource_name: string;
  resource_group?: string;
  details?: string;
}

export interface Plan {
  planId: string;
  title: string;
  operations: PlanOperation[];
  riskLevel: "Low" | "Medium" | "High";
  estimatedCostNote?: string;
  status?: "pending" | "approved" | "rejected";
}

export interface ChatMessage {
  role: "user" | "agent";
  content: string;
  toolCalls?: ToolCall[];
  plan?: Plan;
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
  | { type: "plan"; data: { plan_id: string; title: string; operations: PlanOperation[]; risk_level: string; estimated_cost_note?: string; session_id: string } }
  | { type: "reply"; data: { content: string; session_id: string } }
  | { type: "usage"; data: { input_tokens: number; output_tokens: number; session_id: string } }
  | { type: "error"; data: { message: string; session_id: string } };
