from __future__ import annotations

import base64
from dataclasses import dataclass
from urllib.parse import quote

import aiohttp
from fastapi import HTTPException


@dataclass(frozen=True)
class AzureDevOpsConfig:
    org_url: str
    project: str
    repository: str
    pat: str
    branch: str = "main"
    file_path: str = "infra/desired-state.json"


def _normalize_path(file_path: str) -> str:
    return file_path if file_path.startswith("/") else f"/{file_path}"


def _normalize_branch(branch: str) -> str:
    prefix = "refs/heads/"
    return branch[len(prefix):] if branch.lower().startswith(prefix) else branch


def _api_base(cfg: AzureDevOpsConfig) -> str:
    return (
        f"{cfg.org_url.rstrip('/')}/{quote(cfg.project, safe='')}"
        f"/_apis/git/repositories/{quote(cfg.repository, safe='')}"
    )


def _headers(cfg: AzureDevOpsConfig) -> dict[str, str]:
    token = base64.b64encode(f":{cfg.pat}".encode("ascii")).decode("ascii")
    return {"Authorization": f"Basic {token}", "Accept": "application/json"}


async def _ensure_success(response: aiohttp.ClientResponse, operation: str) -> None:
    if 200 <= response.status < 300:
        return
    body = await response.text()
    raise HTTPException(
        status_code=400,
        detail=f"{operation} failed ({response.status} {response.reason}): {body}",
    )


async def get_desired_state(cfg: AzureDevOpsConfig) -> str | None:
    path = quote(_normalize_path(cfg.file_path), safe="")
    branch = quote(_normalize_branch(cfg.branch), safe="")
    versioned_url = (
        f"{_api_base(cfg)}/items?path={path}&$format=text"
        f"&versionDescriptor.version={branch}&versionDescriptor.versionType=branch&api-version=7.0"
    )
    default_url = f"{_api_base(cfg)}/items?path={path}&$format=text&api-version=7.0"

    async with aiohttp.ClientSession(headers=_headers(cfg)) as session:
        async with session.get(versioned_url) as response:
            if response.status == 404 and _normalize_branch(cfg.branch).lower() == "main":
                async with session.get(default_url) as default_response:
                    if default_response.status == 404:
                        return None
                    await _ensure_success(default_response, "Azure DevOps item fetch")
                    return await default_response.text()
            if response.status == 404:
                return None
            await _ensure_success(response, "Azure DevOps item fetch")
            return await response.text()


async def _branch_object_id(session: aiohttp.ClientSession, cfg: AzureDevOpsConfig, branch: str) -> str | None:
    refs_url = f"{_api_base(cfg)}/refs?filter={quote(f'heads/{branch}', safe='')}&api-version=7.0"
    async with session.get(refs_url) as response:
        await _ensure_success(response, "Azure DevOps refs fetch")
        refs_json = await response.json()
    values = refs_json.get("value") if isinstance(refs_json, dict) else None
    if isinstance(values, list) and values:
        object_id = values[0].get("objectId")
        return object_id if isinstance(object_id, str) else None
    return None


async def _push_file_change(
    session: aiohttp.ClientSession,
    cfg: AzureDevOpsConfig,
    branch: str,
    old_object_id: str,
    commit_message: str,
    change_type: str,
    content: str,
) -> aiohttp.ClientResponse:
    push = {
        "refUpdates": [{"name": f"refs/heads/{branch}", "oldObjectId": old_object_id}],
        "commits": [
            {
                "comment": commit_message,
                "changes": [
                    {
                        "changeType": change_type,
                        "item": {"path": _normalize_path(cfg.file_path)},
                        "newContent": {"content": content, "contentType": "rawtext"},
                    }
                ],
            }
        ],
    }
    push_url = f"{_api_base(cfg)}/pushes?api-version=7.0"
    return await session.post(push_url, json=push)


async def push_desired_state(cfg: AzureDevOpsConfig, content: str, commit_message: str) -> None:
    branch = _normalize_branch(cfg.branch)
    async with aiohttp.ClientSession(headers=_headers(cfg)) as session:
        old_object_id = await _branch_object_id(session, cfg, branch)
        if old_object_id is None and branch.lower() != "main":
            old_object_id = await _branch_object_id(session, cfg, "main")
            old_object_id = old_object_id or "0000000000000000000000000000000000000000"
        elif old_object_id is None:
            raise HTTPException(status_code=400, detail=f"Azure DevOps branch '{branch}' was not found.")

        existing = await get_desired_state(cfg)
        change_type = "add" if existing is None else "edit"
        response = await _push_file_change(session, cfg, branch, old_object_id, commit_message, change_type, content)
        if not (200 <= response.status < 300) and change_type == "add":
            error_body = await response.text()
            if "specified in the add operation already exists" in error_body.lower():
                old_object_id = await _branch_object_id(session, cfg, branch) or old_object_id
                response = await _push_file_change(session, cfg, branch, old_object_id, commit_message, "edit", content)
            else:
                raise HTTPException(
                    status_code=400,
                    detail=f"Azure DevOps push failed ({response.status} {response.reason}): {error_body}",
                )
        await _ensure_success(response, "Azure DevOps push")
