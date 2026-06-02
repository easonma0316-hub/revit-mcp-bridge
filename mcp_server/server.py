"""RevitMCP - MCP server bridging Claude to the Revit 2024 add-in over HTTP.

Architecture:
    Claude Code --stdio--> this process --HTTP--> RevitMCP.Addin (inside Revit)
                                                       |
                                            ExternalEvent -> Revit API (UI thread)

The add-in runs an HTTP listener at REVIT_MCP_URL. Each tool here forwards a
{"command": ..., "params": ...} payload and unwraps the {"ok", "result"} reply.
"""
import os

import httpx
from mcp.server.fastmcp import FastMCP

REVIT_URL = os.environ.get("REVIT_MCP_URL", "http://127.0.0.1:8765/")

mcp = FastMCP("revit")


def _call(command: str, params: dict | None = None) -> dict:
    payload = {"command": command, "params": params or {}}
    with httpx.Client(timeout=60) as client:
        resp = client.post(REVIT_URL, json=payload)
        resp.raise_for_status()
        data = resp.json()
    if not data.get("ok"):
        raise RuntimeError(data.get("error", "Unknown error from Revit."))
    return data.get("result")


@mcp.tool()
def ping() -> dict:
    """Check the Revit connection; returns version and active document info."""
    return _call("ping")


@mcp.tool()
def get_selection() -> dict:
    """Return the elements currently selected in Revit (id, name, category, class)."""
    return _call("get_selection")


@mcp.tool()
def get_element_info(element_id: int) -> dict:
    """Return a summary and all parameters of a Revit element by its id."""
    return _call("get_element_info", {"id": element_id})


@mcp.tool()
def set_parameter(element_id: int, name: str, value: str) -> dict:
    """Set a parameter on a Revit element. Runs inside a Revit transaction."""
    return _call("set_parameter", {"id": element_id, "name": name, "value": value})


if __name__ == "__main__":
    mcp.run()  # stdio transport; Claude Code launches this process
