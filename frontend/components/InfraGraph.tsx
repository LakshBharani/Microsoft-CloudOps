"use client";

import { useCallback, useMemo } from "react";
import {
  ReactFlow,
  Background,
  Controls,
  type Node,
  type Edge,
  type NodeProps,
  Handle,
  Position,
  BackgroundVariant,
} from "@xyflow/react";
import { FolderOpen } from "lucide-react";
import type { InfrastructureGraph, ResourceNode } from "@/lib/types";
import { getResourceMeta } from "@/lib/resourceMeta";

const RG_TYPE = "microsoft.resources/resourcegroups";

// Layout constants
const NODE_W = 200;
const NODE_H = 90;
const PAD_X = 30;
const PAD_TOP = 48;
const PAD_BOTTOM = 30;
const GAP_X = 20;
const GAP_Y = 20;
const GROUP_GAP = 60;
const MIN_GROUP_W = 270;
const MIN_GROUP_H = 130;

const DIFF_RING: Record<string, string> = {
  create: "#22c55e",
  update: "#f59e0b",
  delete: "#ef4444",
};

function ResourceNodeComponent({ data }: NodeProps) {
  const meta = getResourceMeta(data.type as string);
  const diffRing = data.diffRing as string | undefined;
  return (
    <div
      className="rounded-lg border px-3 py-2 min-w-[140px] max-w-[180px] shadow-lg cursor-pointer"
      style={{ borderColor: diffRing ?? meta.color, background: meta.bg }}
    >
      <Handle type="target" position={Position.Top} className="!bg-slate-500" />
      <div className="text-[10px] font-semibold mb-0.5" style={{ color: meta.color }}>
        {meta.label}
      </div>
      <div className="text-xs text-white font-medium truncate" title={data.name as string}>
        {data.name as string}
      </div>
      <div className="text-[10px] text-slate-400 truncate">{data.location as string}</div>
      <Handle type="source" position={Position.Bottom} className="!bg-slate-500" />
    </div>
  );
}

function ResourceGroupNode({ data }: NodeProps) {
  const diffRing = data.diffRing as string | undefined;
  return (
    <div
      style={{
        width: "100%",
        height: "100%",
        border: `1.5px dashed ${diffRing ?? "#334155"}`,
        background: "rgba(30, 41, 59, 0.2)",
        borderRadius: "12px",
        pointerEvents: "none",
      }}
    >
      <div
        className="flex items-center gap-1.5 px-3 pt-2.5"
        style={{ pointerEvents: "auto" }}
      >
        <FolderOpen size={12} className="text-slate-500 flex-shrink-0" />
        <span className="text-[11px] font-semibold text-slate-400 truncate">
          {data.name as string}
        </span>
      </div>
    </div>
  );
}

const nodeTypes = {
  resource: ResourceNodeComponent,
  resourceGroup: ResourceGroupNode,
};

function buildGroupedNodes(
  nodes: ResourceNode[],
  diffStatus: Record<string, "create" | "update" | "delete">
): Node[] {
  const rgNodes = nodes.filter((n) => n.type.toLowerCase() === RG_TYPE);
  const resourceNodes = nodes.filter((n) => n.type.toLowerCase() !== RG_TYPE);

  const childMap = new Map<string, ResourceNode[]>();
  for (const rg of rgNodes) {
    childMap.set(rg.name.toLowerCase(), []);
  }

  const ungrouped: ResourceNode[] = [];
  for (const node of resourceNodes) {
    const key = node.resourceGroup.toLowerCase();
    if (childMap.has(key)) {
      childMap.get(key)!.push(node);
    } else {
      ungrouped.push(node);
    }
  }

  const result: Node[] = [];
  let groupX = 0;
  let maxGroupH = 0;

  for (const rg of rgNodes) {
    const children = childMap.get(rg.name.toLowerCase()) ?? [];
    const cols = Math.max(1, Math.ceil(Math.sqrt(Math.max(children.length, 1))));
    const rows = Math.max(1, Math.ceil(children.length / cols));

    const innerW = cols * NODE_W + (cols - 1) * GAP_X;
    const innerH = rows * NODE_H + (rows - 1) * GAP_Y;
    const groupW = Math.max(MIN_GROUP_W, innerW + PAD_X * 2);
    const groupH = Math.max(MIN_GROUP_H, innerH + PAD_TOP + PAD_BOTTOM);

    // Group node must come before its children in the array
    result.push({
      id: rg.id,
      type: "resourceGroup",
      position: { x: groupX, y: 0 },
      data: { name: rg.name, raw: rg, diffRing: DIFF_RING[diffStatus[rg.id] ?? ""] },
      style: { width: groupW, height: groupH },
    });

    children.forEach((child, i) => {
      const col = i % cols;
      const row = Math.floor(i / cols);
      result.push({
        id: child.id,
        type: "resource",
        parentId: rg.id,
        extent: "parent" as const,
        position: {
          x: PAD_X + col * (NODE_W + GAP_X),
          y: PAD_TOP + row * (NODE_H + GAP_Y),
        },
        data: {
          name: child.name,
          type: child.type,
          location: child.location,
          raw: child,
          diffRing: DIFF_RING[diffStatus[child.id] ?? ""],
        },
      });
    });

    groupX += groupW + GROUP_GAP;
    maxGroupH = Math.max(maxGroupH, groupH);
  }

  // Resources whose RG isn't in the graph — lay out below all groups
  const uCols = Math.max(1, Math.ceil(Math.sqrt(ungrouped.length)));
  const ungroupedOffsetY = maxGroupH > 0 ? maxGroupH + GROUP_GAP : 0;
  ungrouped.forEach((node, i) => {
    result.push({
      id: node.id,
      type: "resource",
      position: {
        x: (i % uCols) * (NODE_W + GAP_X),
        y: ungroupedOffsetY + Math.floor(i / uCols) * (NODE_H + GAP_Y),
      },
      data: {
        name: node.name,
        type: node.type,
        location: node.location,
        raw: node,
        diffRing: DIFF_RING[diffStatus[node.id] ?? ""],
      },
    });
  });

  return result;
}

interface Props {
  graph: InfrastructureGraph;
  onNodeClick: (node: ResourceNode) => void;
  diffStatus?: Record<string, "create" | "update" | "delete">;
}

export default function InfraGraph({ graph, onNodeClick, diffStatus = {} }: Props) {
  const rgIds = useMemo(
    () => new Set(graph.nodes.filter((n) => n.type.toLowerCase() === RG_TYPE).map((n) => n.id)),
    [graph.nodes]
  );

  const flowNodes = useMemo(
    () => buildGroupedNodes(graph.nodes, diffStatus),
    [graph.nodes, diffStatus]
  );

  const flowEdges: Edge[] = useMemo(
    () =>
      graph.edges
        .filter((e) => !rgIds.has(e.sourceId) && !rgIds.has(e.targetId))
        .map((e, i) => ({
          id: `e-${i}`,
          source: e.sourceId,
          target: e.targetId,
          label: e.dependencyType,
          animated: e.dependencyType === "hosting",
          style: {
            stroke: (e.riskWeight ?? 0) >= 0.8 ? "#f87171" : "#475569",
            strokeWidth: 1.5,
          },
          labelStyle: { fill: "#94a3b8", fontSize: 10 },
          labelBgStyle: { fill: "#1e293b" },
        })),
    [graph.edges, rgIds]
  );

  const handleNodeClick = useCallback(
    (_: React.MouseEvent, node: Node) => {
      if (node.type === "resourceGroup") return;
      onNodeClick(node.data.raw as ResourceNode);
    },
    [onNodeClick]
  );

  return (
    <ReactFlow
      nodes={flowNodes}
      edges={flowEdges}
      nodeTypes={nodeTypes}
      onNodeClick={handleNodeClick}
      fitView
      fitViewOptions={{ padding: 0.15 }}
      className="!bg-[#0f1117]"
    >
      <Background variant={BackgroundVariant.Dots} color="#1e293b" gap={20} />
      <Controls className="!bg-[#1e293b] !border-slate-700 !text-slate-300" />
    </ReactFlow>
  );
}
