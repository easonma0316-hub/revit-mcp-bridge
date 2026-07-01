"""RevitMCP - MCP server bridging Claude to the Revit add-in over HTTP.

Architecture:
    Claude / MCP client --stdio--> this process --HTTP--> RevitMCP.Addin (in Revit)
                                                              |
                                                   ExternalEvent -> Revit API (UI thread)

The add-in runs an HTTP listener at REVIT_MCP_URL. Each tool forwards a
{"command": ..., "params": ...} payload and unwraps the reply, which is either
{"ok": true, "result": ...} or {"ok": false, "error": ..., "code": ...}.

Environment:
    REVIT_MCP_URL        add-in listener URL (default http://127.0.0.1:8765/).
                         If Revit bound a fallback port, set this to match.
    REVIT_MCP_TIMEOUT    per-request HTTP timeout in seconds (default 65).

All coordinates and lengths returned are Revit internal units (feet).
"""
import os

import httpx
from mcp.server.fastmcp import FastMCP

REVIT_URL = os.environ.get("REVIT_MCP_URL", "http://127.0.0.1:8765/")
# Slightly above the add-in's UI-thread timeout (60s) so the transport doesn't
# give up before Revit does.
TIMEOUT = float(os.environ.get("REVIT_MCP_TIMEOUT", "65"))

mcp = FastMCP("revit")


class RevitError(RuntimeError):
    """A structured error returned by the Revit add-in (carries a machine code)."""

    def __init__(self, code: str, message: str):
        self.code = code
        super().__init__(f"[{code}] {message}")


def _call(command: str, params: dict | None = None) -> dict:
    """POST one command to the add-in and return its result, or raise."""
    payload = {"command": command, "params": params or {}}
    try:
        with httpx.Client(timeout=TIMEOUT) as client:
            resp = client.post(REVIT_URL, json=payload)
            resp.raise_for_status()
            data = resp.json()
    except httpx.ConnectError as exc:
        raise RevitError(
            "NOT_CONNECTED",
            f"Cannot reach the Revit add-in at {REVIT_URL}. Make sure Revit is "
            "open, a model is loaded, and the RevitMCP add-in started (check its "
            "log; set REVIT_MCP_URL if Revit used a fallback port).",
        ) from exc
    except httpx.TimeoutException as exc:
        raise RevitError(
            "TIMEOUT",
            f"Revit did not respond within {TIMEOUT}s. It may be busy or showing "
            "a modal dialog that is blocking its UI thread.",
        ) from exc
    except httpx.HTTPError as exc:
        raise RevitError("HTTP_ERROR", f"HTTP error talking to Revit: {exc}") from exc

    if not data.get("ok"):
        raise RevitError(data.get("code", "ERROR"), data.get("error", "Unknown error from Revit."))
    return data.get("result")


# ============================ read tools =================================


@mcp.tool()
def ping() -> dict:
    """Check the Revit connection. Returns service info, Revit version, the active
    document title, and whether the bridge is in read-only mode. Call this first
    if anything seems wrong."""
    return _call("ping")


@mcp.tool()
def get_model_info() -> dict:
    """Return a summary of the open model: title, file path, worksharing status,
    active view, and counts of elements, views, and levels."""
    return _call("get_model_info")


@mcp.tool()
def list_categories() -> dict:
    """List every Revit category present in the model with an instance count,
    sorted most-common first. Use this to discover what a model contains before
    querying elements."""
    return _call("list_categories")


@mcp.tool()
def query_elements(
    category: str | None = None,
    name_contains: str | None = None,
    limit: int = 200,
) -> dict:
    """Find element instances in the model.

    Args:
        category: A category to filter by. Accepts a display name ("Walls",
            "Doors") or a BuiltInCategory ("OST_Walls"). Omit for all categories.
        name_contains: Only return elements whose name contains this text.
        limit: Max elements to return (default 200). 'truncated' is true if more
            matched than were returned.

    Returns element summaries (id, name, category, class). Use get_element_info
    for full details on one element.
    """
    params: dict = {"limit": limit}
    if category:
        params["category"] = category
    if name_contains:
        params["name_contains"] = name_contains
    return _call("query_elements", params)


@mcp.tool()
def get_element_info(element_id: int) -> dict:
    """Return full details for one element: summary, type, level, location,
    bounding box, and all parameters. Coordinates are in feet."""
    return _call("get_element_info", {"id": element_id})


@mcp.tool()
def get_parameter(element_id: int, name: str) -> dict:
    """Read a single parameter on an element, including its raw value, display
    value, storage type, and whether it is read-only."""
    return _call("get_parameter", {"id": element_id, "name": name})


@mcp.tool()
def get_selection() -> dict:
    """Return the elements currently selected in Revit (id, name, category, class)."""
    return _call("get_selection")


@mcp.tool()
def list_views() -> dict:
    """List all non-template views in the model (id, name, view type)."""
    return _call("list_views")


@mcp.tool()
def get_active_view() -> dict:
    """Return the currently active view (id, name, view type)."""
    return _call("get_active_view")


@mcp.tool()
def list_levels() -> dict:
    """List the model's levels (id, name, elevation in feet), lowest first."""
    return _call("list_levels")


# ============================ write tools ================================
# Tools that change the model (set_parameter, color_elements, delete_elements)
# fail with code READ_ONLY when the add-in runs in read-only mode. Selection and
# temporary view isolate are UI/view-only and stay available.


@mcp.tool()
def set_parameter(name: str, value: str, element_id: int | None = None,
                  element_ids: list[int] | None = None) -> dict:
    """Set a parameter to the same value on one or many elements, in a single
    Revit transaction.

    Provide either element_id (one) or element_ids (many). Numeric parameters
    accept numeric strings; length values are interpreted in Revit internal
    units (feet).
    """
    params: dict = {"name": name, "value": value}
    if element_ids:
        params["ids"] = element_ids
    elif element_id is not None:
        params["id"] = element_id
    else:
        raise RevitError("BAD_REQUEST", "Provide element_id or element_ids.")
    return _call("set_parameter", params)


@mcp.tool()
def set_selection(element_ids: list[int]) -> dict:
    """Select the given elements in Revit's UI. Useful to visually confirm a set
    of elements before acting on them."""
    return _call("set_selection", {"ids": element_ids})


@mcp.tool()
def color_elements(element_ids: list[int], r: int = 255, g: int = 0, b: int = 0) -> dict:
    """Override the color of elements in the active view (RGB 0-255, default red).
    This is a reversible graphic override, great for visually highlighting query
    results. Reset it in Revit via View > Reset graphic overrides."""
    return _call("color_elements", {"ids": element_ids, "r": r, "g": g, "b": b})


@mcp.tool()
def isolate_elements(element_ids: list[int]) -> dict:
    """Temporarily isolate elements in the active view (hides everything else).
    Fully reversible — call reset_view to restore the normal view."""
    return _call("isolate_elements", {"ids": element_ids})


@mcp.tool()
def reset_view() -> dict:
    """Clear temporary hide/isolate on the active view, restoring normal
    visibility."""
    return _call("reset_view")


@mcp.tool()
def delete_elements(element_ids: list[int]) -> dict:
    """Delete elements from the model. By default Revit shows a confirmation
    dialog before deleting; if the user declines, this returns error code
    CANCELLED. Returns how many elements were actually removed (including
    dependents)."""
    return _call("delete_elements", {"ids": element_ids})


if __name__ == "__main__":
    mcp.run()  # stdio transport; the MCP client launches this process
