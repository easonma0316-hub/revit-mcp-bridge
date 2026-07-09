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

# Opt-in gate for the execute_code tool (arbitrary C# inside Revit). Off by
# default; set REVIT_MCP_ENABLE_CODE=1 in the MCP server's env to enable it.
ENABLE_CODE = os.environ.get("REVIT_MCP_ENABLE_CODE", "").lower() in ("1", "true", "yes")

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
    """Set a parameter on a Revit element. Runs inside a Revit transaction.

    For numeric (length/area/angle) parameters, pass the value in the model's
    *display* units (e.g. 3000 for 3000 mm); the add-in converts to Revit's
    internal units. Returns the stored value with its unit-aware display string.
    """
    return _call("set_parameter", {"id": element_id, "name": name, "value": value})


@mcp.tool()
def list_levels() -> dict:
    """List all levels in the model with their elevations in millimeters."""
    return _call("list_levels")


@mcp.tool()
def list_views() -> dict:
    """List the model's views (id, name, type); the active view is flagged isActive."""
    return _call("list_views")


@mcp.tool()
def list_categories() -> dict:
    """Count model elements grouped by category — a quick statistical overview."""
    return _call("list_categories")


@mcp.tool()
def get_elements(category: str, limit: int = 100) -> dict:
    """List elements of a category (exact name, e.g. 'Walls', 'Doors' — see
    list_categories). Returns up to `limit` summaries plus the total count."""
    return _call("get_elements", {"category": category, "limit": limit})


@mcp.tool()
def list_family_types(category: str | None = None, contains: str | None = None,
                      limit: int = 200) -> dict:
    """List loadable family types (id, category, family, type) available for
    place_family_instance. Filter by exact category name and/or a substring of
    the family/type name."""
    return _call("list_family_types",
                 {"category": category, "contains": contains, "limit": limit})


@mcp.tool()
def export_view_image(view_id: int | None = None, pixels: int = 1600) -> dict:
    """Export a view (default: the active view) as a PNG and return its file
    path — read that file to see the model. Use list_views to find view ids."""
    return _call("export_view_image", {"view_id": view_id, "pixels": pixels})


@mcp.tool()
def select_elements(ids: list[int]) -> dict:
    """Select (highlight) the given element ids in the Revit UI."""
    return _call("select_elements", {"ids": ids})


@mcp.tool()
def delete_elements(ids: list[int]) -> dict:
    """Delete elements by id. Dependent elements (tags, ...) are deleted too,
    so the deleted count can exceed the requested count. Runs in a transaction."""
    return _call("delete_elements", {"ids": ids})


@mcp.tool()
def create_wall(start: list[float], end: list[float], level_id: int,
                height_mm: float, type_id: int | None = None) -> dict:
    """Create a straight wall from `start` to `end` ([x, y] in millimeters, model
    coordinates) on the given level, `height_mm` high. `type_id` is a wall type
    id (get_elements category 'Walls' shows instances; omit for the default type).
    Runs in a transaction; returns the new element's summary."""
    return _call("create_wall", {"start": start, "end": end, "level_id": level_id,
                                 "height_mm": height_mm, "type_id": type_id})


@mcp.tool()
def place_family_instance(type_id: int, point: list[float],
                          level_id: int | None = None) -> dict:
    """Place a family instance (door/window needs a host — use freestanding
    families like furniture) at `point` ([x, y, z] in millimeters). `type_id`
    comes from list_family_types. Runs in a transaction."""
    return _call("place_family_instance",
                 {"type_id": type_id, "point": point, "level_id": level_id})


@mcp.tool()
def get_project_info() -> dict:
    """Return project metadata: title, file path, project name/number, client,
    address, workshared flag and the model's length display unit."""
    return _call("get_project_info")


@mcp.tool()
def get_view_elements(view_id: int | None = None, limit: int = 100) -> dict:
    """List the elements visible in a view (default: the active view), with a
    per-category count breakdown and up to `limit` element summaries."""
    return _call("get_view_elements", {"view_id": view_id, "limit": limit})


@mcp.tool()
def filter_elements(category: str | None = None, name_contains: str | None = None,
                    level_id: int | None = None, limit: int = 100) -> dict:
    """Find model elements by any combination of exact category name, a
    substring of the element name, and/or level id. At least one filter is
    required. Returns up to `limit` summaries plus the total match count."""
    return _call("filter_elements", {"category": category, "name_contains": name_contains,
                                     "level_id": level_id, "limit": limit})


@mcp.tool()
def get_location(element_id: int) -> dict:
    """Return an element's spatial data in millimeters: location point (with
    rotation) or curve endpoints/length, plus its bounding box. Use this to
    place or move things relative to existing elements."""
    return _call("get_location", {"id": element_id})


@mcp.tool()
def move_elements(ids: list[int], vector: list[float]) -> dict:
    """Move elements by `vector` ([x, y] or [x, y, z] in millimeters).
    Runs in a transaction."""
    return _call("move_elements", {"ids": ids, "vector": vector})


@mcp.tool()
def copy_elements(ids: list[int], vector: list[float]) -> dict:
    """Copy elements, offset by `vector` ([x, y] or [x, y, z] in millimeters).
    Returns the new element ids. Runs in a transaction."""
    return _call("copy_elements", {"ids": ids, "vector": vector})


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
def create_floor(boundary: list[list[float]], level_id: int,
                 type_id: int | None = None) -> dict:
    """Create a floor on a level from `boundary` — at least 3 [x, y] points in
    millimeters forming a closed, non-self-intersecting loop (do not repeat the
    first point). Omit `type_id` for the default floor type. Runs in a transaction."""
    return _call("create_floor", {"boundary": boundary, "level_id": level_id,
                                  "type_id": type_id})


@mcp.tool()
def create_room(level_id: int, point: list[float]) -> dict:
    """Place a room at `point` ([x, y] in millimeters) on a level. An area of
    0 m2 means the point is not enclosed by room-bounding elements (walls).
    Runs in a transaction."""
    return _call("create_room", {"level_id": level_id, "point": point})


@mcp.tool()
def set_active_view(view_id: int) -> dict:
    """Switch the Revit UI to the given view (see list_views). The change is
    applied immediately after this call returns."""
    return _call("set_active_view", {"view_id": view_id})


@mcp.tool()
def color_elements(ids: list[int], rgb: list[int] | None = None,
                   view_id: int | None = None, clear: bool = False) -> dict:
    """Color elements in a view (default: active view) with a solid fill +
    line override, e.g. rgb=[255, 0, 0] to flag them red. Pass clear=true
    (rgb not needed) to remove the overrides. Runs in a transaction."""
    return _call("color_elements", {"ids": ids, "rgb": rgb, "view_id": view_id,
                                    "clear": clear})


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
          XYZ -> [x, y, z] mm, other types -> ToString())."""
        return _call("execute_code", {"code": code})


if __name__ == "__main__":
    mcp.run()  # stdio transport; Claude Code launches this process
