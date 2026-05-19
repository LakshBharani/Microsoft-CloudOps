from __future__ import annotations

from app.models import DesiredResourceNode, DesiredStateSpec, ResourceNode


def _key(name: str, type_: str, resource_group: str) -> str:
    return f"{name.lower()}|{type_.lower()}|{resource_group.lower()}"


def _to_diff_node(node: DesiredResourceNode, existing_id: str | None = None) -> dict[str, object]:
    data: dict[str, object] = {
        "name": node.name,
        "type": node.type,
        "resourceGroup": node.resourceGroup,
        "location": node.location,
        "changes": [],
    }
    if existing_id is not None:
        data["existingId"] = existing_id
    return data


def _detect_changes(desired: DesiredResourceNode, live: ResourceNode) -> list[dict[str, str | None]]:
    changes: list[dict[str, str | None]] = []
    if desired.location and desired.location.lower() != live.location.lower():
        changes.append({"field": "location", "from": live.location, "to": desired.location})
    if desired.skuJson is not None and (live.skuJson or "").lower() != desired.skuJson.lower():
        changes.append({"field": "sku", "from": live.skuJson, "to": desired.skuJson})
    if desired.kind is not None and (live.kind or "").lower() != desired.kind.lower():
        changes.append({"field": "kind", "from": live.kind, "to": desired.kind})
    for key, value in desired.tags.items():
        live_value = live.tags.get(key)
        if live_value != value:
            changes.append({"field": f"tag:{key}", "from": live_value, "to": value})
    return changes


def _is_scoped_resource_group(node: ResourceNode, scoped_rgs: set[str]) -> bool:
    return node.type.lower() == "microsoft.resources/resourcegroups" and node.name.lower() in scoped_rgs


def _is_managed_by_desired_state(live_node: ResourceNode, desired: DesiredStateSpec) -> bool:
    if not desired.nodes:
        return False

    tag_values: dict[str, set[str]] = {}
    tag_key_lookup: dict[str, str] = {}
    for node in desired.nodes:
        for key, value in node.tags.items():
            lowered = key.lower()
            tag_key_lookup.setdefault(lowered, key)
            tag_values.setdefault(lowered, set()).add(value.lower())

    stable_tags = {
        tag_key_lookup[key]: next(iter(values))
        for key, values in tag_values.items()
        if len(values) == 1
    }
    if not stable_tags:
        return False

    live_tags = {key.lower(): value.lower() for key, value in live_node.tags.items()}
    return all(live_tags.get(key.lower()) == value for key, value in stable_tags.items())


def compute_diff(live_nodes: list[ResourceNode], desired: DesiredStateSpec) -> dict[str, list[dict[str, object]]]:
    scoped_rgs = {item.lower() for item in desired.scope} if desired.scope else {
        node.resourceGroup.lower() for node in desired.nodes
    }
    live_in_scope = [node for node in live_nodes if node.resourceGroup.lower() in scoped_rgs]
    live_index = {_key(node.name, node.type, node.resourceGroup): node for node in live_in_scope}
    matched_live_keys: set[str] = set()

    result: dict[str, list[dict[str, object]]] = {
        "toCreate": [],
        "toUpdate": [],
        "toDelete": [],
        "unchanged": [],
    }

    for desired_node in desired.nodes:
        key = _key(desired_node.name, desired_node.type, desired_node.resourceGroup)
        live_node = live_index.get(key)
        if live_node is None:
            result["toCreate"].append(_to_diff_node(desired_node))
            continue

        matched_live_keys.add(key)
        diff_node = _to_diff_node(desired_node, live_node.id)
        changes = _detect_changes(desired_node, live_node)
        diff_node["changes"] = changes
        result["toUpdate" if changes else "unchanged"].append(diff_node)

    for live_node in live_in_scope:
        if _is_scoped_resource_group(live_node, scoped_rgs):
            continue
        if not _is_managed_by_desired_state(live_node, desired):
            continue

        key = _key(live_node.name, live_node.type, live_node.resourceGroup)
        if key not in matched_live_keys:
            result["toDelete"].append(
                {
                    "name": live_node.name,
                    "type": live_node.type,
                    "resourceGroup": live_node.resourceGroup,
                    "location": live_node.location,
                    "existingId": live_node.id,
                    "changes": [],
                }
            )

    return result
