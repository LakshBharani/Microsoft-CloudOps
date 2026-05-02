export interface ResourceMeta {
  label: string;
  color: string;
  bg: string;
}

const TYPE_MAP: Record<string, ResourceMeta> = {
  "microsoft.resources/resourcegroups":      { label: "Resource Group", color: "#94a3b8", bg: "#334155" },
  "microsoft.compute/virtualmachines":       { label: "VM",           color: "#60a5fa", bg: "#1e3a5f" },
  "microsoft.network/virtualnetworks":       { label: "VNet",         color: "#34d399", bg: "#064e3b" },
  "microsoft.network/networksecuritygroups": { label: "NSG",          color: "#fbbf24", bg: "#451a03" },
  "microsoft.network/publicipaddresses":     { label: "Public IP",    color: "#a78bfa", bg: "#2e1065" },
  "microsoft.network/networkinterfaces":     { label: "NIC",          color: "#f472b6", bg: "#500724" },
  "microsoft.network/privateendpoints":      { label: "Private EP",   color: "#fb923c", bg: "#431407" },
  "microsoft.storage/storageaccounts":       { label: "Storage",      color: "#38bdf8", bg: "#0c4a6e" },
  "microsoft.web/sites":                     { label: "App Service",  color: "#4ade80", bg: "#052e16" },
  "microsoft.web/serverfarms":               { label: "App Plan",     color: "#86efac", bg: "#052e16" },
  "microsoft.keyvault/vaults":               { label: "Key Vault",    color: "#fcd34d", bg: "#422006" },
  "microsoft.insights/components":           { label: "App Insights", color: "#f87171", bg: "#450a0a" },
  "microsoft.operationalinsights/workspaces":{ label: "Log Analytics",color: "#c084fc", bg: "#3b0764" },
  "microsoft.insights/actiongroups":         { label: "Action Group", color: "#94a3b8", bg: "#1e293b" },
  "microsoft.network/privatednszones":       { label: "DNS Zone",     color: "#67e8f9", bg: "#164e63" },
};

const FALLBACK: ResourceMeta = { label: "Resource", color: "#94a3b8", bg: "#1e293b" };

export function getResourceMeta(type: string): ResourceMeta {
  return TYPE_MAP[type.toLowerCase()] ?? FALLBACK;
}

export function shortType(type: string): string {
  const parts = type.split("/");
  return parts[parts.length - 1] ?? type;
}
