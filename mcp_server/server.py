"""RevitMCP - MCP server bridging Claude to the Revit add-in over HTTP.

Architecture:
    Claude / MCP client --stdio--> this process --HTTP--> RevitMCP.Addin (in Revit)
                                                              |
                                                   ExternalEvent -> Revit API (UI thread)

The add-in runs an HTTP listener at REVIT_MCP_URL. Each tool forwards a
{"command": ..., "params": ...} payload and unwraps the reply, which is either
{"ok": true, "result": ...} or {"ok": false, "error": ..., "code": ...}.

Environment:
    REVIT_MCP_URL          add-in listener URL (default http://127.0.0.1:8765/).
                           If Revit bound a fallback port, set this to match.
    REVIT_MCP_TIMEOUT      per-request HTTP timeout in seconds (default 65).
    REVIT_MCP_ENABLE_CODE  "1" registers the execute_code tool (arbitrary C#
                           inside Revit). Off by default — see the README.

All coordinates and lengths cross this API in millimeters; parameter values use
the model's display units (what the Revit UI shows).
"""
import os

import httpx
from mcp.server.fastmcp import FastMCP

REVIT_URL = os.environ.get("REVIT_MCP_URL", "http://127.0.0.1:8765/")
# Slightly above the add-in's UI-thread timeout (60s) so the transport doesn't
# give up before Revit does.
TIMEOUT = float(os.environ.get("REVIT_MCP_TIMEOUT", "65"))

# Opt-in gate for the execute_code tool (arbitrary C# inside Revit). Off by
# default; set REVIT_MCP_ENABLE_CODE=1 in the MCP server's env to enable it.
ENABLE_CODE = os.environ.get("REVIT_MCP_ENABLE_CODE", "").lower() in ("1", "true", "yes")

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
    """Return a summary of the open model: title, file path, project name/number,
    client, address, worksharing status, active view, display length unit, and
    counts of elements, views, and levels."""
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
    level_id: int | None = None,
    limit: int = 200,
) -> dict:
    """Find element instances in the model.

    Args:
        category: A category to filter by. Accepts a display name ("Walls",
            "Doors") or a BuiltInCategory ("OST_Walls"). Omit for all categories.
        name_contains: Only return elements whose name contains this text.
        level_id: Only return elements hosted on this level (see list_levels).
        limit: Max elements to return (default 200). 'truncated' is true if more
            matched than were returned.

    Returns element summaries (id, name, category, class, level). Use
    get_element_info for full details on one element.
    """
    params: dict = {"limit": limit}
    if category:
        params["category"] = category
    if name_contains:
        params["name_contains"] = name_contains
    if level_id is not None:
        params["level_id"] = level_id
    return _call("query_elements", params)


@mcp.tool()
def get_element_info(element_id: int) -> dict:
    """Return full details for one element: summary, type, level, location
    (point + rotation, or curve endpoints + length), bounding box, and all
    parameters. Coordinates are in millimeters."""
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
    """List all non-template views in the model (id, name, view type); the
    active view is flagged isActive."""
    return _call("list_views")


@mcp.tool()
def get_active_view() -> dict:
    """Return the currently active view (id, name, view type)."""
    return _call("get_active_view")


@mcp.tool()
def list_levels() -> dict:
    """List the model's levels (id, name, elevation in millimeters), lowest
    first."""
    return _call("list_levels")


@mcp.tool()
def list_family_types(category: str | None = None, contains: str | None = None,
                      limit: int = 200) -> dict:
    """List loadable family types (id, category, family, type) available for
    place_family_instance. Filter by exact category name and/or a substring of
    the family/type name."""
    return _call("list_family_types",
                 {"category": category, "contains": contains, "limit": limit})


@mcp.tool()
def list_families(contains: str | None = None, limit: int = 100) -> dict:
    """List the family *definitions* present in the document (id, name,
    category, typeCount, isInPlace) — not placed instances. Filter by a name
    substring. Rename one with rename_element."""
    return _call("list_families", {"contains": contains, "limit": limit})


@mcp.tool()
def get_view_elements(view_id: int | None = None, limit: int = 100) -> dict:
    """List the elements visible in a view (default: the active view), with a
    per-category count breakdown and up to `limit` element summaries."""
    return _call("get_view_elements", {"view_id": view_id, "limit": limit})


@mcp.tool()
def export_view_image(view_id: int | None = None, pixels: int = 1600) -> dict:
    """Export a view (default: the active view) as a PNG and return its file
    path — read that file to see the model. Use list_views to find view ids."""
    return _call("export_view_image", {"view_id": view_id, "pixels": pixels})


# ============================ write tools ================================
# Tools that change the model fail with code READ_ONLY when the add-in runs in
# read-only mode. Selection, view switching, and temporary isolate are UI-only
# and stay available.


@mcp.tool()
def set_parameter(name: str, value: str, element_id: int | None = None,
                  element_ids: list[int] | None = None) -> dict:
    """Set a parameter to the same value on one or many elements, in a single
    Revit transaction.

    Provide either element_id (one) or element_ids (many). For numeric
    (length/area/angle) parameters, pass the value in the model's *display*
    units (e.g. 3000 for 3000 mm); the add-in converts to Revit's internal
    units. Returns the stored values with unit-aware display strings.
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
def color_elements(element_ids: list[int], r: int = 255, g: int = 0, b: int = 0,
                   view_id: int | None = None, clear: bool = False) -> dict:
    """Override the color of elements in a view (default: active view) with a
    solid fill + line color (RGB 0-255, default red) — great for visually
    flagging query results. Pass clear=true (r/g/b ignored) to remove the
    overrides again."""
    return _call("color_elements", {"ids": element_ids, "r": r, "g": g, "b": b,
                                    "view_id": view_id, "clear": clear})


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
def set_active_view(view_id: int) -> dict:
    """Switch the Revit UI to the given view (see list_views). The change is
    applied immediately after this call returns."""
    return _call("set_active_view", {"view_id": view_id})


@mcp.tool()
def delete_elements(element_ids: list[int]) -> dict:
    """Delete elements from the model. By default Revit shows a confirmation
    dialog before deleting; if the user declines, this returns error code
    CANCELLED. Returns how many elements were actually removed (including
    dependents)."""
    return _call("delete_elements", {"ids": element_ids})


@mcp.tool()
def move_elements(element_ids: list[int], vector: list[float]) -> dict:
    """Move elements by `vector` ([x, y] or [x, y, z] in millimeters).
    Runs in a transaction."""
    return _call("move_elements", {"ids": element_ids, "vector": vector})


@mcp.tool()
def copy_elements(element_ids: list[int], vector: list[float]) -> dict:
    """Copy elements, offset by `vector` ([x, y] or [x, y, z] in millimeters).
    Returns the new element ids. Runs in a transaction."""
    return _call("copy_elements", {"ids": element_ids, "vector": vector})


@mcp.tool()
def create_wall(start: list[float], end: list[float], level_id: int,
                height_mm: float, type_id: int | None = None) -> dict:
    """Create a straight wall from `start` to `end` ([x, y] in millimeters, model
    coordinates) on the given level, `height_mm` high. `type_id` is a wall type
    id (omit for the default type). Runs in a transaction; returns the new
    element's summary."""
    return _call("create_wall", {"start": start, "end": end, "level_id": level_id,
                                 "height_mm": height_mm, "type_id": type_id})


@mcp.tool()
def create_floor(boundary: list[list[float]], level_id: int,
                 type_id: int | None = None) -> dict:
    """Create a floor on a level from `boundary` — at least 3 [x, y] points in
    millimeters forming a closed, non-self-intersecting loop (do not repeat the
    first point). Omit `type_id` for the default floor type. Runs in a transaction."""
    return _call("create_floor", {"boundary": boundary, "level_id": level_id,
                                  "type_id": type_id})


@mcp.tool()
def create_level(name: str, elevation_mm: float) -> dict:
    """Create a level at `elevation_mm` named `name` (must be unique).
    Runs in a transaction."""
    return _call("create_level", {"name": name, "elevation_mm": elevation_mm})


@mcp.tool()
def create_grid(start: list[float], end: list[float], name: str | None = None) -> dict:
    """Create a straight grid line from `start` to `end` ([x, y] in millimeters).
    `name` overrides the auto-assigned grid name. Runs in a transaction."""
    return _call("create_grid", {"start": start, "end": end, "name": name})


@mcp.tool()
def create_room(level_id: int, point: list[float]) -> dict:
    """Place a room at `point` ([x, y] in millimeters) on a level. An area of
    0 m2 means the point is not enclosed by room-bounding elements (walls).
    Runs in a transaction."""
    return _call("create_room", {"level_id": level_id, "point": point})


@mcp.tool()
def place_family_instance(type_id: int, point: list[float],
                          level_id: int | None = None) -> dict:
    """Place a family instance (door/window needs a host — use freestanding
    families like furniture) at `point` ([x, y, z] in millimeters). `type_id`
    comes from list_family_types. Runs in a transaction."""
    return _call("place_family_instance",
                 {"type_id": type_id, "point": point, "level_id": level_id})


@mcp.tool()
def rename_element(element_id: int, new_name: str) -> dict:
    """Rename a Revit element by setting its Name *property* (which
    set_parameter cannot reach): families (ids from list_families), family
    types in a project (ids from list_family_types), views, levels, grids,
    materials, ... Fails with BAD_REQUEST if the name is taken or invalid.
    Runs in a transaction."""
    return _call("rename_element", {"id": element_id, "new_name": new_name})


@mcp.tool()
def rename_family_type(new_name: str, type_name: str | None = None) -> dict:
    """FAMILY DOCUMENTS ONLY (family editor): rename a type of the family being
    edited — these types live in the FamilyManager and have no element id.
    `type_name` picks the type (default: the current one). In a *project*,
    rename a family type with rename_element instead."""
    return _call("rename_family_type", {"type_name": type_name, "new_name": new_name})


@mcp.tool()
def save_family_as(path: str, overwrite: bool = False) -> dict:
    """FAMILY DOCUMENTS ONLY: save the open family under a new absolute .rfa
    path. A family's own name IS its file name, so this is the API's way to
    rename the family itself. The old file stays on disk; the open document
    switches to the new path."""
    return _call("save_family_as", {"path": path, "overwrite": overwrite})


if ENABLE_CODE:
    @mcp.tool()
    def execute_code(code: str) -> dict:
        """Run raw C# against the open Revit model (only for what no other tool
        can do). `code` is the BODY of a method with `app` (UIApplication),
        `uidoc` (UIDocument) and `doc` (Document) in scope and must end with a
        `return` statement (`return null;` if there is nothing to report).

        Rules:
        - Already wrapped in a committed Transaction — do NOT open your own
          (SubTransactions are fine). An exception rolls everything back.
        - C# 5 syntax only (no `$"..."` interpolation, no `?.`).
        - Usings available: System, System.Collections.Generic, System.Linq,
          Autodesk.Revit.DB(+.Architecture/.Structure), Autodesk.Revit.UI.
        - Return values are JSON-ified (Element -> summary, ElementId -> id,
          XYZ -> {x, y, z} mm, other types -> ToString())."""
        return _call("execute_code", {"code": code})


def main() -> None:
    """Console-script entry point (see pyproject.toml). Runs the stdio server."""
    mcp.run()  # stdio transport; the MCP client launches this process


if __name__ == "__main__":
    main()
