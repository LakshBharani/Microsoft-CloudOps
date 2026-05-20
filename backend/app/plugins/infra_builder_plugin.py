from __future__ import annotations

import asyncio
import json
import re
import uuid
from contextvars import ContextVar
from typing import Annotated, Any, Awaitable, Callable

import aiohttp
from azure.identity.aio import DefaultAzureCredential
from semantic_kernel.functions import kernel_function

from app.azure_resources import get_infrastructure_nodes
from app.plugins.infra_reader_plugin import _resolve_subscription_id

ToolEventHandler = Callable[[str, str, str, bool | None], Awaitable[None]]
_tool_event_handler: ContextVar[ToolEventHandler | None] = ContextVar(
    "infra-builder_tool_event_handler", default=None
)

MANAGEMENT_SCOPE = "https://management.azure.com/.default"
MANAGEMENT_ENDPOINT = "https://management.azure.com"
DEPLOYMENT_API_VERSION = "2021-04-01"
RESOURCE_GROUP_API_VERSION = "2022-09-01"
ARM_TEMPLATE_PATTERN = re.compile(
    r"<arm-template>\s*(?P<json>\{.*?\})\s*</arm-template>",
    re.IGNORECASE | re.DOTALL,
)


def set_infra_builder_tool_event_handler(handler: ToolEventHandler | None) -> object:
    return _tool_event_handler.set(handler)


def reset_infra_builder_tool_event_handler(token: object) -> None:
    _tool_event_handler.reset(token)


async def _emit_tool_event(tool: str, invocation_id: str, phase: str, success: bool | None = None) -> None:
    handler = _tool_event_handler.get()
    if handler:
        await handler(tool, invocation_id, phase, success)


def _json(data: Any) -> str:
    return json.dumps(data, separators=(",", ":"), default=str)


def _wrapped_arm_template(template: dict[str, Any]) -> str:
    return f"<arm-template>{_json(template)}</arm-template>"


def _parse_json_object(value: str, label: str) -> dict[str, Any]:
    text = value.strip()
    match = ARM_TEMPLATE_PATTERN.search(text)
    if match:
        text = match.group("json")

    try:
        parsed = json.loads(text)
    except json.JSONDecodeError as exc:
        try:
            parsed, _ = json.JSONDecoder().raw_decode(text)
        except json.JSONDecodeError:
            json_start = text.find("{")
            json_end = text.rfind("}")
            if json_start < 0 or json_end <= json_start:
                raise ValueError(f"{label} must be valid JSON.") from exc
            try:
                parsed = json.loads(text[json_start: json_end + 1])
            except json.JSONDecodeError as second_exc:
                raise ValueError(f"{label} must be valid JSON.") from second_exc

    if not isinstance(parsed, dict):
        raise ValueError(f"{label} must be a JSON object.")
    return parsed


def _parse_arm_template(value: str) -> dict[str, Any]:
    template = _parse_json_object(value, "arm_template_json")
    resources = template.get("resources")
    if not isinstance(resources, list):
        raise ValueError("arm_template_json must be an ARM template with a resources array.")
    template.setdefault(
        "$schema",
        "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
    )
    template.setdefault("contentVersion", "1.0.0.0")
    return template


def _resource_group_template(resource_group_name: str, location: str, tags: dict[str, Any]) -> dict[str, Any]:
    return {
        "$schema": "https://schema.management.azure.com/schemas/2018-05-01/subscriptionDeploymentTemplate.json#",
        "contentVersion": "1.0.0.0",
        "resources": [
            {
                "type": "Microsoft.Resources/resourceGroups",
                "apiVersion": RESOURCE_GROUP_API_VERSION,
                "name": resource_group_name,
                "location": location,
                "tags": tags,
            }
        ],
    }


def _delete_operation_template(operation: str, target: dict[str, Any]) -> dict[str, Any]:
    return {
        "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
        "contentVersion": "1.0.0.0",
        "resources": [],
        "metadata": {
            "operation": operation,
            "target": target,
            "execution": "Azure Resource Manager DELETE API",
            "reason": "Single-resource deletion is not safely represented by generic complete-mode ARM deployments.",
        },
    }


async def _authorized_headers(credential: DefaultAzureCredential) -> dict[str, str]:
    token = await credential.get_token(MANAGEMENT_SCOPE)
    return {
        "Authorization": f"Bearer {token.token}",
        "Content-Type": "application/json",
    }


async def _raise_for_status(response: aiohttp.ClientResponse) -> Any:
    text = await response.text()
    try:
        body = json.loads(text) if text else None
    except json.JSONDecodeError:
        body = text or None
    if response.status >= 400:
        raise RuntimeError(f"Azure management request failed ({response.status}): {body}")
    return body


async def _poll_url(
    session: aiohttp.ClientSession,
    headers: dict[str, str],
    url: str,
) -> Any:
    for _ in range(300):
        async with session.get(url, headers=headers) as response:
            if response.status == 204:
                return {"status": "Succeeded"}
            body = await _raise_for_status(response)
            status = ""
            if isinstance(body, dict):
                status = str(
                    body.get("status")
                    or body.get("properties", {}).get("provisioningState")
                    or ""
                ).lower()
            if status in {"succeeded", "failed", "canceled", "cancelled"}:
                if status != "succeeded":
                    raise RuntimeError(f"Azure operation ended with status {status}: {body}")
                return body
        await asyncio.sleep(2)
    raise TimeoutError("Azure operation polling timed out.")


async def _wait_until_not_found(
    session: aiohttp.ClientSession,
    headers: dict[str, str],
    url: str,
) -> dict[str, str]:
    for _ in range(300):
        async with session.get(url, headers=headers) as response:
            if response.status == 404:
                return {"status": "Deleted"}
            await _raise_for_status(response)
        await asyncio.sleep(2)
    raise TimeoutError("Azure delete verification timed out.")


async def _poll_deployment(
    session: aiohttp.ClientSession,
    headers: dict[str, str],
    deployment_url: str,
) -> Any:
    while True:
        async with session.get(deployment_url, headers=headers) as response:
            body = await _raise_for_status(response)
            state = ""
            if isinstance(body, dict):
                state = str(body.get("properties", {}).get("provisioningState") or "").lower()
            if state in {"succeeded", "failed", "canceled", "cancelled"}:
                if state != "succeeded":
                    raise RuntimeError(f"Deployment ended with state {state}: {body}")
                return body
        await asyncio.sleep(2)


async def _put_deployment(
    subscription_id: str,
    deployment_url: str,
    location: str,
    template: dict[str, Any],
    include_location: bool,
) -> dict[str, Any]:
    body = {
        "properties": {
            "mode": "Incremental",
            "template": template,
            "parameters": {},
        },
    }
    if include_location:
        body["location"] = location

    async with DefaultAzureCredential() as credential:
        headers = await _authorized_headers(credential)
        async with aiohttp.ClientSession() as session:
            async with session.put(deployment_url, headers=headers, json=body) as response:
                result = await _raise_for_status(response)
                operation_url = response.headers.get("Azure-AsyncOperation")

            if operation_url:
                await _poll_url(session, headers, operation_url)

            final = await _poll_deployment(session, headers, deployment_url)
            return {
                "subscriptionId": subscription_id,
                "deployment": final,
            }


async def _delete_with_retry(
    url: str,
    max_attempts: int = 8,
    wait_for_operation: bool = True,
    wait_for_not_found: bool = True,
) -> dict[str, Any]:
    delay = 3.0
    last_exc: RuntimeError | None = None
    for attempt in range(max_attempts):
        try:
            return await _delete_management_resource(
                url,
                wait_for_operation=wait_for_operation,
                wait_for_not_found=wait_for_not_found,
            )
        except RuntimeError as exc:
            message = str(exc)
            if "AnotherOperationInProgress" not in message:
                raise
            last_exc = exc
            if attempt == max_attempts - 1:
                break
            await asyncio.sleep(delay)
            delay = min(delay * 1.5, 30.0)
    raise last_exc or RuntimeError("delete retry exhausted")


async def _delete_management_resource(
    url: str,
    wait_for_operation: bool = True,
    wait_for_not_found: bool = True,
) -> dict[str, Any]:
    async with DefaultAzureCredential() as credential:
        headers = await _authorized_headers(credential)
        async with aiohttp.ClientSession() as session:
            async with session.delete(url, headers=headers) as response:
                if response.status == 404:
                    return {
                        "result": {"status": "AlreadyDeleted"},
                        "verification": {"status": "Deleted"},
                    }
                result = await _raise_for_status(response)
                operation_url = response.headers.get("Azure-AsyncOperation") or response.headers.get("Location")

            if operation_url and wait_for_operation:
                try:
                    result = await _poll_url(session, headers, operation_url)
                except TimeoutError:
                    result = {"status": "Operation polling timed out; verifying target deletion directly."}
            elif operation_url:
                result = {"status": "DeleteAccepted", "operationUrl": operation_url}
            else:
                result = result or {"status": "DeleteAccepted"}

            if not wait_for_not_found:
                return {
                    "result": result,
                    "verification": {"status": "DeleteAccepted"},
                }

            deleted = await _wait_until_not_found(session, headers, url)
            return {"result": result, "verification": deleted}


def _subscription_deployment_url(subscription_id: str, deployment_name: str) -> str:
    return (
        f"{MANAGEMENT_ENDPOINT}/subscriptions/{subscription_id}"
        f"/providers/Microsoft.Resources/deployments/{deployment_name}"
        f"?api-version={DEPLOYMENT_API_VERSION}"
    )


def _resource_group_deployment_url(subscription_id: str, resource_group_name: str, deployment_name: str) -> str:
    return (
        f"{MANAGEMENT_ENDPOINT}/subscriptions/{subscription_id}/resourcegroups/{resource_group_name}"
        f"/providers/Microsoft.Resources/deployments/{deployment_name}"
        f"?api-version={DEPLOYMENT_API_VERSION}"
    )


def _resource_url(resource_id: str, api_version: str) -> str:
    normalized = resource_id.strip()
    if not normalized.startswith("/"):
        raise ValueError("resource_id must be a full ARM resource id.")
    return f"{MANAGEMENT_ENDPOINT}{normalized}?api-version={api_version.strip()}"


def _deployment_name(prefix: str) -> str:
    return f"im-{prefix}-{uuid.uuid4().hex[:10]}"


def _repair_arm_template(template: dict[str, Any]) -> dict[str, Any]:
    repaired = dict(template)
    repaired.setdefault(
        "$schema",
        "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
    )
    repaired.setdefault("contentVersion", "1.0.0.0")
    repaired.setdefault("resources", [])
    if not isinstance(repaired["resources"], list):
        raise ValueError("ARM template resources must be a JSON array.")
    repaired.setdefault("parameters", {})
    repaired.setdefault("variables", {})
    return repaired


async def _deploy_to_resource_group(
    subscription_id: str,
    resource_group_name: str,
    location: str,
    template: dict[str, Any],
    prefix: str,
) -> dict[str, Any]:
    deployment_name = _deployment_name(prefix)
    result: dict[str, Any] | None = None
    for attempt in range(1, 7):
        try:
            result = await _put_deployment(
                subscription_id,
                _resource_group_deployment_url(subscription_id, resource_group_name, deployment_name),
                location,
                template,
                include_location=False,
            )
            break
        except RuntimeError as exc:
            if "ResourceGroupNotFound" not in str(exc) or attempt == 6:
                raise
            await asyncio.sleep(3)
    if result is None:
        raise RuntimeError("Resource group deployment did not return a result.")
    return {"deploymentName": deployment_name, "result": result}


async def _resource_group_exists(subscription_id: str, resource_group_name: str) -> bool:
    nodes = await get_infrastructure_nodes(subscription_id)
    expected = resource_group_name.strip().lower()
    return any(
        node.type.lower() == "microsoft.resources/resourcegroups"
        and node.name.lower() == expected
        for node in nodes
    )


async def _resource_exists(
    subscription_id: str,
    resource_id: str = "",
    resource_group_name: str = "",
    resource_type: str = "",
    resource_name: str = "",
) -> bool:
    nodes = await get_infrastructure_nodes(subscription_id)
    expected_id = resource_id.strip().lower()
    expected_group = resource_group_name.strip().lower()
    expected_type = resource_type.strip().lower()
    expected_name = resource_name.strip().lower()
    for node in nodes:
        if expected_id and node.id.lower() == expected_id:
            return True
        if expected_id:
            continue
        if expected_group and node.resourceGroup.lower() != expected_group:
            continue
        if expected_type and node.type.lower() != expected_type:
            continue
        if expected_name and node.name.lower() != expected_name:
            continue
        if expected_group or expected_type or expected_name:
            return True
    return False


class InfraBuilderPlugin:
    """Azure execution tools for infra-builder-agent."""

    @kernel_function(name="create_resource_group", description="Create or update an Azure resource group using a subscription-scope ARM deployment.")
    async def create_resource_group(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Resource group name to create"],
        location: Annotated[str, "Azure region for the resource group"],
        tags_json: Annotated[str, "Optional JSON object of tags"] = "{}",
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("create_resource_group", invocation_id, "start")
        try:
            resolved_subscription_id = _resolve_subscription_id(subscription_id)
            tags = _parse_json_object(tags_json, "tags_json")
            template = _resource_group_template(resource_group_name, location, tags)
            deployment_name = _deployment_name("rg")
            result = await _put_deployment(
                resolved_subscription_id,
                _subscription_deployment_url(resolved_subscription_id, deployment_name),
                location,
                template,
                include_location=True,
            )
            await _emit_tool_event("create_resource_group", invocation_id, "end", True)
            return _json(
                {
                    "status": "succeeded",
                    "deploymentName": deployment_name,
                    "armTemplate": _wrapped_arm_template(template),
                    "result": result,
                }
            )
        except Exception:
            await _emit_tool_event("create_resource_group", invocation_id, "end", False)
            raise

    @kernel_function(name="deploy_resource", description="Deploy an ARM template for one resource-level create operation inside an existing resource group.")
    async def deploy_resource(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Target resource group name"],
        location: Annotated[str, "Azure region for the deployment record"],
        arm_template_json: Annotated[str, "ARM template JSON, optionally wrapped in <arm-template> tags"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("deploy_resource", invocation_id, "start")
        try:
            resolved_subscription_id = _resolve_subscription_id(subscription_id)
            template = _parse_arm_template(arm_template_json)
            deployment = await _deploy_to_resource_group(
                resolved_subscription_id,
                resource_group_name,
                location,
                template,
                "deploy",
            )
            await _emit_tool_event("deploy_resource", invocation_id, "end", True)
            return _json(
                {
                    "status": "succeeded",
                    "deploymentName": deployment["deploymentName"],
                    "armTemplate": _wrapped_arm_template(template),
                    "result": deployment["result"],
                }
            )
        except Exception:
            await _emit_tool_event("deploy_resource", invocation_id, "end", False)
            raise

    @kernel_function(name="update_resource", description="Update an Azure resource by redeploying the desired ARM template slice in incremental mode.")
    async def update_resource(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Target resource group name"],
        location: Annotated[str, "Azure region for the deployment record"],
        arm_template_json: Annotated[str, "ARM template JSON, optionally wrapped in <arm-template> tags"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("update_resource", invocation_id, "start")
        try:
            resolved_subscription_id = _resolve_subscription_id(subscription_id)
            template = _parse_arm_template(arm_template_json)
            deployment = await _deploy_to_resource_group(
                resolved_subscription_id,
                resource_group_name,
                location,
                template,
                "update",
            )
            await _emit_tool_event("update_resource", invocation_id, "end", True)
            return _json(
                {
                    "status": "succeeded",
                    "deploymentName": deployment["deploymentName"],
                    "armTemplate": _wrapped_arm_template(template),
                    "result": deployment["result"],
                }
            )
        except Exception:
            await _emit_tool_event("update_resource", invocation_id, "end", False)
            raise

    @kernel_function(name="delete_resource", description="Delete one Azure resource by full ARM resource id using Azure Resource Manager.")
    async def delete_resource(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_id: Annotated[str, "Full ARM resource id to delete"],
        api_version: Annotated[str, "Azure API version for this resource type"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("delete_resource", invocation_id, "start")
        try:
            _resolve_subscription_id(subscription_id)
            operation_template = _delete_operation_template(
                "delete_resource",
                {"resourceId": resource_id, "apiVersion": api_version},
            )
            result = await _delete_with_retry(_resource_url(resource_id, api_version))
            await _emit_tool_event("delete_resource", invocation_id, "end", True)
            return _json(
                {
                    "status": "succeeded",
                    "armTemplate": _wrapped_arm_template(operation_template),
                    "result": result,
                }
            )
        except Exception:
            await _emit_tool_event("delete_resource", invocation_id, "end", False)
            raise

    @kernel_function(name="delete_resource_group", description="Delete an Azure resource group using Azure Resource Manager.")
    async def delete_resource_group(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Resource group name to delete"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("delete_resource_group", invocation_id, "start")
        try:
            resolved_subscription_id = _resolve_subscription_id(subscription_id)
            operation_template = _delete_operation_template(
                "delete_resource_group",
                {"resourceGroup": resource_group_name},
            )
            url = (
                f"{MANAGEMENT_ENDPOINT}/subscriptions/{resolved_subscription_id}"
                f"/resourcegroups/{resource_group_name}?api-version={RESOURCE_GROUP_API_VERSION}"
            )
            result = await _delete_with_retry(
                url,
                wait_for_operation=False,
                wait_for_not_found=False,
            )
            await _emit_tool_event("delete_resource_group", invocation_id, "end", True)
            return _json(
                {
                    "status": "succeeded",
                    "armTemplate": _wrapped_arm_template(operation_template),
                    "result": result,
                }
            )
        except Exception:
            await _emit_tool_event("delete_resource_group", invocation_id, "end", False)
            raise

    @kernel_function(name="rethink_deployment", description="Repair and redeploy a failed ARM template after reading the Azure error. Use this after deploy_resource or update_resource fails.")
    async def rethink_deployment(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Target resource group name"],
        location: Annotated[str, "Azure region for the deployment record"],
        failed_arm_template_json: Annotated[str, "The failed ARM template JSON, optionally wrapped in <arm-template> tags"],
        error_message: Annotated[str, "The Azure deployment error message"],
        corrected_arm_template_json: Annotated[
            str,
            "Optional corrected ARM template JSON after reasoning about the error. If empty, the tool applies safe structural repairs to the failed template.",
        ] = "",
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("rethink_deployment", invocation_id, "start")
        try:
            resolved_subscription_id = _resolve_subscription_id(subscription_id)
            source_template = corrected_arm_template_json.strip() or failed_arm_template_json
            template = _repair_arm_template(_parse_arm_template(source_template))
            deployment = await _deploy_to_resource_group(
                resolved_subscription_id,
                resource_group_name,
                location,
                template,
                "retry",
            )
            await _emit_tool_event("rethink_deployment", invocation_id, "end", True)
            return _json(
                {
                    "status": "succeeded",
                    "deploymentName": deployment["deploymentName"],
                    "previousError": error_message,
                    "armTemplate": _wrapped_arm_template(template),
                    "result": deployment["result"],
                }
            )
        except Exception:
            await _emit_tool_event("rethink_deployment", invocation_id, "end", False)
            raise

    @kernel_function(name="verify_resource_exists", description="Verify whether a resource exists after an execution step.")
    async def verify_resource_exists(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_id: Annotated[str, "Optional full ARM resource id"] = "",
        resource_group_name: Annotated[str, "Optional resource group name"] = "",
        resource_type: Annotated[str, "Optional Azure resource type"] = "",
        resource_name: Annotated[str, "Optional Azure resource name"] = "",
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("verify_resource_exists", invocation_id, "start")
        try:
            resolved_subscription_id = _resolve_subscription_id(subscription_id)
            exists = await _resource_exists(
                resolved_subscription_id,
                resource_id,
                resource_group_name,
                resource_type,
                resource_name,
            )
            await _emit_tool_event("verify_resource_exists", invocation_id, "end", True)
            return _json({"exists": exists})
        except Exception:
            await _emit_tool_event("verify_resource_exists", invocation_id, "end", False)
            raise

    @kernel_function(name="verify_resource_group_exists", description="Verify whether a resource group exists after an execution step.")
    async def verify_resource_group_exists(
        self,
        subscription_id: Annotated[str, "Azure subscription ID"],
        resource_group_name: Annotated[str, "Resource group name"],
    ) -> str:
        invocation_id = str(uuid.uuid4())
        await _emit_tool_event("verify_resource_group_exists", invocation_id, "start")
        try:
            resolved_subscription_id = _resolve_subscription_id(subscription_id)
            exists = await _resource_group_exists(resolved_subscription_id, resource_group_name)
            await _emit_tool_event("verify_resource_group_exists", invocation_id, "end", True)
            return _json({"exists": exists})
        except Exception:
            await _emit_tool_event("verify_resource_group_exists", invocation_id, "end", False)
            raise
