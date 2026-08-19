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
    REVIT_MCP_BATCH_TIMEOUT timeout for batch commands such as
                           create_walls_from_cad / create_doors_from_cad with
                           dry_run=False (default 900).
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
# Batch commands (whole floors of walls/doors) run ~1 min per 1000 elements.
BATCH_TIMEOUT = float(os.environ.get("REVIT_MCP_BATCH_TIMEOUT", "900"))

# Opt-in gate for the execute_code tool (arbitrary C# inside Revit). Off by
# default; set REVIT_MCP_ENABLE_CODE=1 in the MCP server's env to enable it.
ENABLE_CODE = os.environ.get("REVIT_MCP_ENABLE_CODE", "").lower() in ("1", "true", "yes")

mcp = FastMCP("revit")


class RevitError(RuntimeError):
    """A structured error returned by the Revit add-in (carries a machine code)."""

    def __init__(self, code: str, message: str):
        self.code = code
        super().__init__(f"[{code}] {message}")


def _call(command: str, params: dict | None = None,
          timeout: float | None = None) -> dict:
    """POST one command to the add-in and return its result, or raise.
    `timeout` (seconds) overrides the default for long batch commands; it is
    also sent to the add-in so its UI-thread wait matches."""
    payload = {"command": command, "params": params or {}}
    http_timeout = TIMEOUT
    if timeout is not None:
        payload["timeout_ms"] = int(timeout * 1000)
        http_timeout = timeout + 5
    try:
        with httpx.Client(timeout=http_timeout) as client:
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
            f"Revit did not respond within {http_timeout}s. It may be busy or showing "
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


# ---- CAD link driven modelling ------------------------------------------------

@mcp.tool()
def list_cad_links(include_layers: bool = True, layer_limit: int = 200) -> dict:
    """List the DWG links/imports (ImportInstance) in the open model. With
    `include_layers`, each link reports its layers with entity counts
    (lines/arcs/polylines/other), sorted by size - use it to spot the wall,
    door and column layers before calling get_cad_geometry or
    create_walls_from_cad. Returns the link `id` the other CAD tools need."""
    return _call("list_cad_links", {"include_layers": include_layers,
                                    "layer_limit": layer_limit})


@mcp.tool()
def get_cad_geometry(link_id: int, layers: list[str] | None = None,
                     bbox_mm: list[list[float]] | None = None,
                     limit: int = 500) -> dict:
    """Read raw curves from a CAD link, in millimeters / model coordinates.
    `layers` filters by layer name (omit for all); `bbox_mm` =
    [[xmin, ymin], [xmax, ymax]] keeps only curves touching that rectangle
    (strongly recommended - whole floors are tens of thousands of entities).
    Each curve has kind = line {start, end} | arc {center, radius_mm, start,
    end, mid, sweep_deg} | circle {center, radius_mm} | polyline {points,
    closed}. Use it to inspect how walls/doors are drawn, or to drive
    create_wall / place_family_instance by hand on a small area."""
    return _call("get_cad_geometry", {"link_id": link_id, "layers": layers,
                                      "bbox_mm": bbox_mm, "limit": limit})


@mcp.tool()
def create_walls_from_cad(link_id: int, layers: list[str], level_id: int,
                          height_mm: float,
                          bbox_mm: list[list[float]] | None = None,
                          dry_run: bool = True,
                          thicknesses_mm: list[float] | None = None,
                          auto_thicknesses: bool = False,
                          tolerance_mm: float = 5.0,
                          min_length_mm: float = 300.0,
                          merge_gap_mm: float = 300.0,
                          type_map: list[dict] | None = None,
                          door_layers: list[str] | None = None,
                          window_layers: list[str] | None = None,
                          max_opening_mm: float = 3000.0,
                          snap_ends: bool = True,
                          create_missing_types: bool = True,
                          ai_tag: str = "_AI",
                          base_type_id: int | None = None,
                          centerline_layers: list[str] | None = None,
                          centerline_thickness_mm: float = 50.0,
                          centerline_material: str = "Glass",
                          centerline_type_id: int | None = None,
                          use_existing_walls: bool = True,
                          skip_existing: bool = True,
                          max_walls: int = 1000) -> dict:
    """Build Revit walls from the wall layer(s) of a CAD link. Top: give
    `top_level_id` (Top Constraint "Up to level", like a hand-modelled wall,
    optional `top_offset_mm`) or an unconnected `height_mm`; `base_offset_mm`
    lifts the base (e.g. onto a finish floor). Algorithm: every
    line / polyline edge on `layers` becomes a segment; parallel segments whose
    distance matches a wall thickness (`thicknesses_mm`, default common
    100..400 mm values, +-`tolerance_mm`) and that overlap by >= `min_length_mm`
    are paired into a centerline of that thickness; collinear centerlines
    closer than `merge_gap_mm` (door/window openings) are merged; each result
    becomes Wall.Create on `level_id`, `height_mm` high, using the basic wall
    type whose width matches (or `type_map` = [{"thickness_mm", "type_id"}]).
    ALWAYS start with `dry_run=True` (default) and a small `bbox_mm`: it
    returns the planned walls (start/end/thickness/type) plus counts of
    unpaired segments and warnings without touching the model. Then rerun with
    dry_run=False. Curved walls: concentric arcs a thickness apart become arc
    walls (Wall.Create with an Arc).
    Pairing rules: only *adjacent* faces pair (nothing in between), and the two
    edges of one polyline outline are preferred, so double walls / cavities are
    not mistaken for a thin wall. Wall ends are extended/trimmed to the
    centerline of the wall they run into (`snap_ends`) so Revit joins them.
    Openings: pass the layer(s) holding door swing arcs as `door_layers`; a gap
    (up to `max_opening_mm`) that contains a swing arc is bridged - the wall
    runs through and the door cuts it later. Likewise `window_layers`: a gap
    filled by window lines drawn along the wall (face / glass lines) is bridged
    so create_windows_from_cad can host a window there. Gaps with neither (or
    without those params) are LEFT OPEN in the wall, as are gaps > merge_gap_mm.
    `auto_thicknesses=True` learns the wall thicknesses from the drawing
    (histogram of adjacent parallel-face spacings, peaks with >= 3 pairs; the
    result is reported as `thicknesses_mm` / `thickness_peaks`) instead of the
    default metric list - use it for imperial or unfamiliar drawings.
    Types: an existing basic wall type within `tolerance_mm` of the thickness
    is reused; otherwise (create_missing_types) the nearest type (or
    `base_type_id`) is duplicated with its core layer resized, named after the
    source's convention with the size swapped + `ai_tag` (e.g.
    SYB_WA_Generic_200mm -> SYB_WA_Generic_250mm_AI) and Type Comments noting
    it was AI-made. Thicknesses are quantised to `thicknesses_mm`, so CAD noise
    cannot spawn dozens of types.
    Centerline mode: `centerline_layers` (e.g. shop-area / shopfront lines that
    have no wall thickness) are taken as wall AXES - each line becomes a
    placeholder wall `centerline_thickness_mm` thick (default 50) of a type
    named by convention with the material word (SYB_WA_Glass_50mm_AI, structure
    layer material = `centerline_material`), or `centerline_type_id`. Lines
    running along real walls (planned or already in the model) are dropped.
    `layers` may be empty when only centerline walls are wanted. Existing walls
    on the level are used for dedupe and end snapping (`use_existing_walls`),
    and walls that already exist are skipped (`skip_existing`)."""
    return _call("create_walls_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id,
        "height_mm": height_mm, "top_level_id": top_level_id,
        "top_offset_mm": top_offset_mm, "base_offset_mm": base_offset_mm,
        "bbox_mm": bbox_mm, "dry_run": dry_run,
        "thicknesses_mm": thicknesses_mm, "auto_thicknesses": auto_thicknesses,
        "tolerance_mm": tolerance_mm,
        "min_length_mm": min_length_mm, "merge_gap_mm": merge_gap_mm,
        "type_map": type_map, "door_layers": door_layers,
        "window_layers": window_layers,
        "max_opening_mm": max_opening_mm, "snap_ends": snap_ends,
        "create_missing_types": create_missing_types, "ai_tag": ai_tag,
        "base_type_id": base_type_id,
        "centerline_layers": centerline_layers,
        "centerline_thickness_mm": centerline_thickness_mm,
        "centerline_material": centerline_material,
        "centerline_type_id": centerline_type_id,
        "use_existing_walls": use_existing_walls, "skip_existing": skip_existing,
        "max_walls": max_walls},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_doors_from_cad(link_id: int, layers: list[str], level_id: int,
                          bbox_mm: list[list[float]] | None = None,
                          dry_run: bool = True,
                          type_map: list[dict] | None = None,
                          create_missing_types: bool = True,
                          single_family: str | None = None,
                          double_family: str | None = None,
                          asymmetric_family: str | None = None,
                          width_tolerance_mm: float = 50.0,
                          width_step_mm: float = 100.0,
                          ai_tag: str = "_AI",
                          host_tolerance_mm: float = 60.0,
                          min_single_width_mm: float = 600.0,
                          skip_existing: bool = True) -> dict:
    """Place hosted doors from the door swing arcs on CAD layer(s) - run AFTER
    the walls exist (create_walls_from_cad). Each quarter-circle arc = one leaf:
    centre = hinge, radius = leaf width; the arc end on the wall is the latch,
    the other end the open tip, which fixes hinge side and swing side. Two arcs
    whose latch points meet become a double (equal leaves) or asymmetric door.
    Host = the straight wall on `level_id` the hinge sits on (within half the
    wall thickness + `host_tolerance_mm`; raise it to ~200 for doors drawn on
    shopfront/area lines that sit a bit off the placeholder wall). Type = `type_map`
    [{"width_mm", "type_id"}] if given, else the loaded door type of matching
    width (families are picked by name hints single/double/asym[metric] or the
    *_family substrings you pass). Type economy: a type within
    `width_tolerance_mm` is reused; else, with `create_missing_types`, the CAD
    width is rounded to `width_step_mm` (1170 -> 1200) and ONE type per grid
    value is duplicated from the nearest type, named by its convention with the
    size swapped + `ai_tag` (W900 x H2100 -> W1200 x H2100_AI, Type Comments say
    AI-made). Asymmetric doors fall back to the nearest type with a warning.
    Hand and
    facing are set by comparing the placed door's own plan swing arc with the
    CAD arc, so family conventions don't matter. Doors within 200 mm of an
    existing door are skipped (`skip_existing`). Start with dry_run=True: it
    lists planned doors (kind/width/centre/host/type), unhosted arcs and
    warnings without touching the model."""
    return _call("create_doors_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id,
        "bbox_mm": bbox_mm, "dry_run": dry_run, "type_map": type_map,
        "create_missing_types": create_missing_types,
        "single_family": single_family, "double_family": double_family,
        "asymmetric_family": asymmetric_family,
        "width_tolerance_mm": width_tolerance_mm,
        "width_step_mm": width_step_mm, "ai_tag": ai_tag,
        "host_tolerance_mm": host_tolerance_mm,
        "min_single_width_mm": min_single_width_mm,
        "skip_existing": skip_existing},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_columns_from_cad(link_id: int, layers: list[str], level_id: int,
                            height_mm: float | None = None,
                            top_level_id: int | None = None,
                            bbox_mm: list[list[float]] | None = None,
                            dry_run: bool = True,
                            structural: bool = True,
                            rect_family: str | None = None,
                            round_family: str | None = None,
                            size_step_mm: float = 50.0,
                            size_tolerance_mm: float = 25.0,
                            create_missing_types: bool = True,
                            ai_tag: str = "_AI",
                            skip_existing: bool = True) -> dict:
    """Place columns from a CAD column layer: every closed 4-corner rectangle
    (perpendicular sides) becomes a rectangular column (longer side = Width,
    rotated to match), every circle (or arcs closing a full turn) a round
    column. Structural columns by default (`structural=False` for
    architectural). Families are picked by name hint (Rectangular/矩形,
    Circular/圆) or `rect_family` / `round_family` substrings; the family must
    expose Width+Depth or Radius/Diameter type parameters. Sizes are rounded to
    `size_step_mm`; a type within `size_tolerance_mm` is reused, else one is
    duplicated from the nearest type and named by its convention
    ("1000x1000mm" -> "600x500mm_AI"; circular "1000mm" is a RADIUS ->
    "125mm_AI" for a 250 mm column) with Type Comments marking it AI-made.
    Extent: base = `level_id`, top = `top_level_id` or base + `height_mm`.
    Duplicated footprints in the drawing and columns already in the model
    (within 100 mm) are skipped. dry_run=True first to see the plan."""
    return _call("create_columns_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id,
        "height_mm": height_mm, "top_level_id": top_level_id,
        "bbox_mm": bbox_mm, "dry_run": dry_run, "structural": structural,
        "rect_family": rect_family, "round_family": round_family,
        "size_step_mm": size_step_mm, "size_tolerance_mm": size_tolerance_mm,
        "create_missing_types": create_missing_types, "ai_tag": ai_tag,
        "skip_existing": skip_existing},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_windows_from_cad(link_id: int, layers: list[str], level_id: int,
                            bbox_mm: list[list[float]] | None = None,
                            dry_run: bool = True,
                            sill_height_mm: float = 900.0,
                            host_tolerance_mm: float = 60.0,
                            min_width_mm: float = 300.0,
                            max_width_mm: float = 6000.0,
                            join_gap_mm: float = 100.0,
                            family: str | None = None,
                            type_map: list[dict] | None = None,
                            width_step_mm: float = 100.0,
                            width_tolerance_mm: float = 50.0,
                            create_missing_types: bool = True,
                            ai_tag: str = "_AI",
                            skip_existing: bool = True) -> dict:
    """Place hosted windows from the window symbol lines on CAD layer(s) - run
    AFTER create_walls_from_cad (called with the same `window_layers`, so the
    walls run through the window openings). A plan window is a bundle of
    lines drawn ALONG the wall across the opening (wall-face cut lines, glass /
    frame lines) plus short jamb lines: every along-wall segment inside a
    wall's corridor (half thickness + `host_tolerance_mm`) is assigned to that
    wall, touching segments (gap <= `join_gap_mm`) merge into one opening; its
    extent is the width, its middle the centre. Width in
    [`min_width_mm`, `max_width_mm`]. Type = `type_map` [{"width_mm",
    "type_id"}] if given, else a loaded window type of matching width
    (families filtered by `family` substring, default hints fixed/casement/
    sliding); a type within `width_tolerance_mm` is reused, else the width is
    rounded to `width_step_mm` and ONE type per grid value is duplicated from
    the nearest type (name by its convention + `ai_tag`, Type Comments say
    AI-made). Sill = `sill_height_mm` above the level. Windows within 200 mm
    of an existing window are skipped. dry_run=True first: lists planned
    windows (width/centre/host/type), unhosted segments and warnings."""
    return _call("create_windows_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id,
        "bbox_mm": bbox_mm, "dry_run": dry_run, "sill_height_mm": sill_height_mm,
        "host_tolerance_mm": host_tolerance_mm, "min_width_mm": min_width_mm,
        "max_width_mm": max_width_mm, "join_gap_mm": join_gap_mm,
        "family": family, "type_map": type_map, "width_step_mm": width_step_mm,
        "width_tolerance_mm": width_tolerance_mm,
        "create_missing_types": create_missing_types, "ai_tag": ai_tag,
        "skip_existing": skip_existing},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_roofs_from_cad(link_id: int, layers: list[str], level_id: int,
                          thickness_mm: float | None = None, offset_mm: float = 0.0,
                          type_id: int | None = None, type_name: str | None = None,
                          min_area_m2: float = 5.0, join_tolerance_mm: float = 25.0,
                          inner_loops_as_openings: bool = True,
                          bbox_mm: list[list[float]] | None = None,
                          dry_run: bool = True) -> dict:
    """Flat footprint roofs from roof outlines on CAD layer(s): closed polylines
    or chains of lines/arcs whose ends meet within `join_tolerance_mm`; loops
    under `min_area_m2` ignored; a loop inside another becomes an opening. Roof
    type: `type_id` / `type_name` (substring) / the default roof type;
    `thickness_mm` duplicates it with the core layer resized ("<type>
    300mm_AI"); `offset_mm` = base offset from the level. If the drawing has no
    usable outline (common in plan exports: only edges outside the parapets
    are drawn), use create_floor_from_walls(kind="roof") instead. dry_run
    lists the loops (area, vertices, bbox, outline points) and the available
    roof types - confirm thickness / type with the user before building."""
    return _call("create_roofs_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id, "thickness_mm": thickness_mm,
        "offset_mm": offset_mm, "type_id": type_id, "type_name": type_name, "min_area_m2": min_area_m2,
        "join_tolerance_mm": join_tolerance_mm, "inner_loops_as_openings": inner_loops_as_openings,
        "bbox_mm": bbox_mm, "dry_run": dry_run},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_railings_from_cad(link_id: int, layers: list[str], level_id: int,
                             type_id: int | None = None, type_name: str | None = None,
                             height_mm: float | None = None,
                             min_segment_mm: float = 150.0, min_path_mm: float = 600.0,
                             join_tolerance_mm: float = 30.0, double_line_mm: float = 120.0,
                             path_duplicate_mm: float = 150.0,
                             exclude_on_stairs: bool = True,
                             exclude_bboxes_mm: list[list[list[float]]] | None = None,
                             base_offset_mm: float = 0.0, skip_existing: bool = True,
                             bbox_mm: list[list[float]] | None = None,
                             dry_run: bool = True) -> dict:
    """Railings from the railing layer(s) of a plan: railings are drawn as thin
    outlines / parallel lines (`double_line_mm` apart) - they are collapsed to
    a midline, chained into paths (ends within `join_tolerance_mm`), short
    symbols (< `min_segment_mm`: balusters, posts) and leftovers (paths <
    `min_path_mm`) dropped, parallel duplicates within `path_duplicate_mm`
    removed. Paths on stair footprints (`exclude_on_stairs`, uses the stairs
    in the model whose base or top is this level) and inside
    `exclude_bboxes_mm` ([[[x0,y0],[x1,y1]], ...]) are skipped - stair
    railings belong to the stairs. Type: `type_id` / `type_name` / nearest
    `height_mm` (railing types and their heights are listed in the result) -
    confirm height/type with the user first. dry_run lists the paths."""
    return _call("create_railings_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id, "type_id": type_id, "type_name": type_name,
        "height_mm": height_mm, "min_segment_mm": min_segment_mm, "min_path_mm": min_path_mm,
        "join_tolerance_mm": join_tolerance_mm, "double_line_mm": double_line_mm, "path_duplicate_mm": path_duplicate_mm,
        "exclude_on_stairs": exclude_on_stairs, "exclude_bboxes_mm": exclude_bboxes_mm,
        "base_offset_mm": base_offset_mm, "skip_existing": skip_existing, "bbox_mm": bbox_mm, "dry_run": dry_run},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_curtain_walls_from_cad(link_id: int, layers: list[str], level_id: int,
                                  height_mm: float | None = None, top_level_id: int | None = None,
                                  top_offset_mm: float = 0.0, base_offset_mm: float = 0.0,
                                  mullion_layers: list[str] | None = None,
                                  type_id: int | None = None, type_name: str | None = None,
                                  mullion_type_id: int | None = None, mullion_type_name: str | None = None,
                                  mullions: bool = True, grid_from_mullions: bool = True,
                                  default_grid_mm: float = 0.0,
                                  min_segment_mm: float = 100.0, join_gap_mm: float = 250.0,
                                  collinear_tolerance_mm: float = 25.0, min_run_mm: float = 600.0,
                                  double_line_mm: float = 200.0, mullion_max_area_m2: float = 0.05,
                                  skip_existing: bool = True,
                                  bbox_mm: list[list[float]] | None = None,
                                  dry_run: bool = True, ai_tag: str = "_AI") -> dict:
    """Curtain walls from glazing lines: collinear segments on `layers` with gaps
    <= `join_gap_mm` (a mullion sits in the gap) form one run (>= `min_run_mm`);
    the two faces of a glazing line (`double_line_mm` apart) collapse into one.
    Small closed polylines (< `mullion_max_area_m2`, on `mullion_layers` or the
    glazing layers) are mullion sections: their positions become the vertical
    grid of the wall (else `default_grid_mm`, 0 = none). Built as Revit curtain
    walls of a type derived from `type_id`/`type_name` (substring; default the
    first 'Curtain Wall' type) named "<type> CAD grid_AI" with manual vertical
    grid and mullions (`mullion_type_name`, default a rectangular one) on grid
    lines and borders. Height: `top_level_id` (+`top_offset_mm`) or
    `height_mm` - the height is NOT in the plan: dry_run first, confirm with
    the user, then build. Existing curtain walls passing through the level
    (multi-storey glazing built once) are not rebuilt."""
    return _call("create_curtain_walls_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id, "height_mm": height_mm,
        "top_level_id": top_level_id, "top_offset_mm": top_offset_mm, "base_offset_mm": base_offset_mm,
        "mullion_layers": mullion_layers, "type_id": type_id, "type_name": type_name,
        "mullion_type_id": mullion_type_id, "mullion_type_name": mullion_type_name, "mullions": mullions,
        "grid_from_mullions": grid_from_mullions, "default_grid_mm": default_grid_mm,
        "min_segment_mm": min_segment_mm, "join_gap_mm": join_gap_mm, "collinear_tolerance_mm": collinear_tolerance_mm,
        "min_run_mm": min_run_mm, "double_line_mm": double_line_mm, "mullion_max_area_m2": mullion_max_area_m2,
        "skip_existing": skip_existing, "bbox_mm": bbox_mm, "dry_run": dry_run, "ai_tag": ai_tag},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def list_warnings(max_ids: int = 20, include_swallowed: bool = True) -> dict:
    """List the document's warnings - what Revit's 'Review Warnings' dialog shows
    (walls overlap, elements joined but not intersecting, duplicate Mark values,
    slightly off axis, room not enclosed, ...), grouped by description with
    counts, categories, involved element ids (up to `max_ids`) and whether
    `fix_warnings` can fix the class automatically. `include_swallowed` adds the
    transient warnings the add-in dismissed while running MCP commands (since
    Revit started). Read-only."""
    return _call("list_warnings", {"max_ids": max_ids, "include_swallowed": include_swallowed})


@mcp.tool()
def fix_warnings(dry_run: bool = True, kinds: list[str] | None = None,
                 duplicate_tolerance_mm: float = 50.0, touch_tolerance_mm: float = 5.0,
                 max_fixes: int = 500) -> dict:
    """Fix the warning classes that are safe to automate: UnjoinDisjoint
    ('joined but do not intersect' -> unjoin), DeleteDuplicateWall ('walls
    overlap' between parallel straight walls: footprints merely touching / grazing
    by <= `touch_tolerance_mm` -> nudge the thinner wall apart by that amount
    (invisible, clears the warning); one wall's footprint entirely inside the
    other's (within `duplicate_tolerance_mm` along, 20 mm across) and hosting no
    doors/windows -> delete it; partial / crossing overlaps are only reported),
    ClearDuplicateMark (duplicate 'Mark' -> clear all but the first). `kinds`
    restricts to some of those names. dry_run=True returns the plan (actions
    + skipped with reasons) without touching the model; dry_run=False applies
    it and reports warnings before/after."""
    return _call("fix_warnings", {"dry_run": dry_run, "kinds": kinds,
                                  "duplicate_tolerance_mm": duplicate_tolerance_mm,
                                  "touch_tolerance_mm": touch_tolerance_mm,
                                  "max_fixes": max_fixes},
                 timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_floor_from_walls(level_id: int, gap_mm: float = 150.0,
                            kind: str = "floor", offset_mm: float = 0.0,
                            link_id: int | None = None,
                            layers: list[str] | None = None,
                            cad_thickness_mm: float = 100.0,
                            min_area_m2: float = 10.0,
                            min_hole_area_m2: float = 0.0,
                            max_hole_area_m2: float = 40.0,
                            exterior_only: bool = False,
                            wall_ids: list[int] | None = None,
                            bbox_mm: list[list[float]] | None = None,
                            type_id: int | None = None,
                            type_name: str | None = None,
                            thickness_mm: float | None = None,
                            dry_run: bool = True,
                            ai_tag: str = "_AI") -> dict:
    """Create the floor slab(s) (kind="floor") or a flat footprint roof
    (kind="roof", type from the roof types, `offset_mm` = base offset) of a
    level from the walls already modelled on it
    (architectural plans rarely draw slab outlines - the outer wall faces are the
    slab edge). Every wall footprint on `level_id` (optionally only `wall_ids`,
    `exterior_only` = Function: Exterior, or inside `bbox_mm`) is inflated by
    `gap_mm` (closes gaps up to 2*gap: unmodelled doors / curtain walls),
    unioned; the outer loop of each connected part >= `min_area_m2` is deflated
    again and becomes one floor. Lines on CAD `layers` of `link_id` (curtain
    walls, shopfront glazing - whatever closes the outline but is not modelled
    as walls) join the union as `cad_thickness_mm` strips. `min_hole_area_m2` > 0 turns enclosed regions
    between min/max hole area into openings (shafts) - 0 = solid slab. Type:
    `type_id`, `type_name` (substring), else the default floor type;
    `thickness_mm` duplicates it as "<type> <mm>mm_AI" with the core layer
    resized. dry_run=True first (area, vertex count, bbox per planned floor)."""
    return _call("create_floor_from_walls", {
        "level_id": level_id, "gap_mm": gap_mm, "kind": kind, "offset_mm": offset_mm,
        "link_id": link_id, "layers": layers,
        "cad_thickness_mm": cad_thickness_mm, "min_area_m2": min_area_m2,
        "min_hole_area_m2": min_hole_area_m2, "max_hole_area_m2": max_hole_area_m2,
        "exterior_only": exterior_only, "wall_ids": wall_ids, "bbox_mm": bbox_mm,
        "type_id": type_id, "type_name": type_name, "thickness_mm": thickness_mm,
        "dry_run": dry_run, "ai_tag": ai_tag},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_floors_from_cad(link_id: int, layers: list[str], level_id: int,
                           min_area_m2: float = 1.0,
                           join_tolerance_mm: float = 10.0,
                           chain_lines: bool = True,
                           inner_loops_as_holes: bool = True,
                           bbox_mm: list[list[float]] | None = None,
                           type_id: int | None = None,
                           type_name: str | None = None,
                           dry_run: bool = True) -> dict:
    """Create floors from closed outlines on CAD layer(s): closed polylines,
    circles, and (chain_lines) closed chains of lines/arcs whose ends meet
    within `join_tolerance_mm`. A loop lying inside another becomes a hole of
    the smallest containing loop (inner_loops_as_holes; islands inside holes
    are floors again). Loops under `min_area_m2` are ignored. Type: `type_id`,
    `type_name` (substring) or the default floor type. dry_run=True first."""
    return _call("create_floors_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id,
        "min_area_m2": min_area_m2, "join_tolerance_mm": join_tolerance_mm,
        "chain_lines": chain_lines, "inner_loops_as_holes": inner_loops_as_holes,
        "bbox_mm": bbox_mm, "type_id": type_id, "type_name": type_name,
        "dry_run": dry_run},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def create_stairs_from_cad(link_id: int, layers: list[str], level_id: int,
                           top_level_id: int | None = None,
                           height_mm: float | None = None,
                           bbox_mm: list[list[float]] | None = None,
                           dry_run: bool = True,
                           arrow_layers: list[str] | None = None,
                           type_id: int | None = None,
                           tread_min_mm: float = 220.0,
                           tread_max_mm: float = 420.0,
                           width_min_mm: float = 600.0,
                           width_max_mm: float = 3500.0,
                           min_treads: int = 3,
                           landing_max_mm: float = 2200.0,
                           skip_existing: bool = True,
                           ai_tag: str = "_AI",
                           from_below: bool = False,
                           base_level_id: int | None = None,
                           break_layers: list[str] | None = None,
                           cut_mark_mm: float = 600.0,
                           nosing_max_mm: float = 100.0,
                           riser_min_mm: float = 130.0,
                           riser_max_mm: float = 220.0,
                           riser_target_mm: float = 170.0,
                           min_risers: int = 5,
                           width_add_mm: float = 0.0) -> dict:
    """Build stairs from the tread lines on CAD stair layer(s): a run is a comb
    of >= `min_treads` parallel, equal-length (= run width), equally spaced
    (= tread depth, within tread_min/max) lines; runs whose ends lie within
    `landing_max_mm` of each other are chained into one stair (U / L shapes get
    an automatic landing). Base = `level_id`, top = `top_level_id` (default:
    the next level up) or `height_mm`; risers = tread lines counted, a stair
    type "<base type> T<tread> R<riser>_AI" is duplicated from `type_id` (or the
    first stair type) with matching min tread / max riser.
    A floor plan shows the stairs GOING UP cut at the view plane (break line)
    and the stairs ARRIVING from the level below complete - so for a
    level-by-level rebuild use `from_below=True` on the plan of the level
    ABOVE: top = `level_id`, base = `base_level_id` or, if omitted, the level
    below whose height gives the most plausible riser (closest to
    `riser_target_mm` within riser_min/max). `width_add_mm` is added to the
    tread-line length (Revit draws treads between the stringers). Nosing doubles (two lines < `nosing_max_mm` apart) and
    superimposed lines are deduped; runs crossed by a break line (zigzag
    polyline on `break_layers` / stair / arrow layers, e.g. A-242-MB_STAIR)
    are dropped (they cannot be modelled from this plan); chains with fewer
    than `min_risers` or an implausible riser height are skipped. Direction: path arrowheads (closed triangle or open
    "V" on `arrow_layers` or the stair layers) - an UP arrow (apex within
    `cut_mark_mm` of a break line) points to the top, a DN arrow (stair from
    below) to the bottom; without one the order is a guess and reported.
    Spiral / winder stairs are not handled. Stairs whose footprint overlaps an
    existing stair on the base level are skipped. Needs its own
    StairsEditScope, so it cannot run inside execute_code's transaction.
    dry_run=True first: lists planned stairs (runs, risers, riser height,
    direction source, bbox)."""
    return _call("create_stairs_from_cad", {
        "link_id": link_id, "layers": layers, "level_id": level_id,
        "top_level_id": top_level_id, "height_mm": height_mm, "bbox_mm": bbox_mm,
        "dry_run": dry_run, "arrow_layers": arrow_layers, "type_id": type_id,
        "tread_min_mm": tread_min_mm, "tread_max_mm": tread_max_mm,
        "width_min_mm": width_min_mm, "width_max_mm": width_max_mm,
        "min_treads": min_treads, "landing_max_mm": landing_max_mm,
        "skip_existing": skip_existing, "ai_tag": ai_tag,
        "from_below": from_below, "base_level_id": base_level_id,
        "break_layers": break_layers, "cut_mark_mm": cut_mark_mm,
        "nosing_max_mm": nosing_max_mm, "riser_min_mm": riser_min_mm,
        "riser_max_mm": riser_max_mm, "riser_target_mm": riser_target_mm,
        "min_risers": min_risers, "width_add_mm": width_add_mm},
        timeout=None if dry_run else BATCH_TIMEOUT)


@mcp.tool()
def snapshot_region(bbox_mm: list[list[float]], pixels: int = 1600,
                    view_id: int | None = None,
                    hide_categories: list[str] | None = None,
                    hide_links: bool = False,
                    highlight: bool = True) -> dict:
    """Export a PNG of a rectangular region [[xmin, ymin], [xmax, ymax]] (mm) of
    a floor plan and return its file path - LOOK at it (open the image) to
    check what was modelled against the CAD. Rendered from a temporary cropped
    copy of the plan (never stale). `view_id` names a floor plan (default: the
    active view, which must be a plan). `highlight=True` paints walls red, doors
    blue, windows green so they stand out from the CAD; `hide_links=True` hides
    the DWG so only Revit elements show; `hide_categories` (e.g. ["Grids",
    "Text Notes"]) hides clutter for the shot only. Use it after
    create_walls_from_cad / create_doors_from_cad / create_columns_from_cad on
    the region you built."""
    return _call("snapshot_region", {"bbox_mm": bbox_mm, "pixels": pixels,
                                     "view_id": view_id,
                                     "hide_categories": hide_categories,
                                     "hide_links": hide_links,
                                     "highlight": highlight})


@mcp.tool()
def open_document(path: str) -> dict:
    """Open a .rvt file (absolute path) and activate it as the active document
    in the Revit UI. Returns title, path, and the name of the resulting active
    view."""
    return _call("open_document", {"path": path})


@mcp.tool()
def create_floor_plan_view(level_id: int, name: str | None = None,
                           ceiling: bool = False, activate: bool = False,
                           scale: int = 0, detail_level: str | None = None) -> dict:
    """Create a floor plan view (or, with `ceiling=True`, a ceiling plan) on
    `level_id`. `name` overrides the auto-assigned view name (must be unique).
    `scale` is the drawing scale denominator (e.g. 100 for 1:100); 0 keeps the
    default. `detail_level` is one of coarse|medium|fine (omit to keep the
    default). `activate=True` switches the Revit UI to the new view. Runs in a
    transaction; returns the new view's id/name/viewType/level."""
    return _call("create_floor_plan_view", {
        "level_id": level_id, "name": name, "ceiling": ceiling,
        "activate": activate, "scale": scale, "detail_level": detail_level})


@mcp.tool()
def link_cad(path: str, view_id: int | None = None, this_view_only: bool = True,
            placement: str = "origin", unit: str = "mm", colors: str = "preserve",
            link: bool = True, pin: bool = True, orient_to_view: bool = False,
            visible_layers_only: bool = False) -> dict:
    """Link (or, with `link=False`, import) a DWG file into the model onto a
    view. `view_id` defaults to the active view, which must be a plan, section,
    elevation, 3D, detail, or drafting view. `placement` is origin|shared|
    center|site; `unit` is mm|cm|m|ft|in|auto (auto uses the DWG's own units);
    `colors` is preserve|bw|invert. `pin=True` (default) pins the link so it
    can't be moved by accident. Returns the link `id` - use it with
    list_cad_links / get_cad_geometry / create_walls_from_cad / etc. - plus the
    target view and bbox_mm. Runs in a transaction; can take a while for large
    DWGs."""
    return _call("link_cad", {
        "path": path, "view_id": view_id, "this_view_only": this_view_only,
        "placement": placement, "unit": unit, "colors": colors, "link": link,
        "pin": pin, "orient_to_view": orient_to_view,
        "visible_layers_only": visible_layers_only}, timeout=BATCH_TIMEOUT)


@mcp.tool()
def export_dwg(folder: str, view_ids: list[int] | None = None,
              view_names: list[str] | None = None, prefix: str = "",
              document: str | None = None, shared_coords: bool = False,
              merged_views: bool = True, export_setup: str | None = None) -> dict:
    """Export views to DWG files (millimeters, R2018) in `folder`, one file per
    view named `<prefix><view name>.dwg`. Give `view_ids` and/or `view_names`
    (by exact name); if both are omitted, exports the active view (only valid
    when `document` is the active document). `document` is the title of any
    currently OPEN document (default: the active one) - no need to activate it
    first. `export_setup` names a predefined DWG export setup in the document
    (error lists the available names if not found) - overrides shared_coords/
    merged_views. Can take a while for many/large views."""
    return _call("export_dwg", {
        "folder": folder, "view_ids": view_ids, "view_names": view_names,
        "prefix": prefix, "document": document, "shared_coords": shared_coords,
        "merged_views": merged_views, "export_setup": export_setup},
        timeout=BATCH_TIMEOUT)


@mcp.tool()
def dump_model(path: str | None = None, categories: list[str] | None = None,
               level_id: int | None = None, level_name: str | None = None,
               bbox_mm: list[list[float]] | None = None,
               include_links: bool = True, document: str | None = None) -> dict:
    """Dump the model's architectural elements (walls, doors, windows, columns,
    stairs, floors, grids) to JSON, in millimeters, host-document coordinates.
    Elements from loaded Revit links are included too (`include_links`), each
    tagged with `source` = the link instance name; use this to compare a
    CAD-rebuilt model against a reference model linked in (or dump the
    reference document directly via `document` = its title among currently
    open documents - no need to activate it). Filter with `categories` (subset
    of walls/doors/windows/columns/stairs/floors/grids), `level_id`/
    `level_name`, and/or `bbox_mm` = [[xmin, ymin], [xmax, ymax]].
    If `path` is given (recommended for whole models - the payload can be
    large), the JSON is written to that file and this returns only
    counts/levels/sources; otherwise the full JSON is returned inline. Pair two
    dumps (ground truth vs. rebuilt) with dev/compare_models.py. Can take a
    while for large models."""
    return _call("dump_model", {
        "path": path, "categories": categories, "level_id": level_id,
        "level_name": level_name, "bbox_mm": bbox_mm,
        "include_links": include_links, "document": document},
        timeout=BATCH_TIMEOUT)


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
    def execute_code(code: str, transaction: bool = True) -> dict:
        """Run raw C# against the open Revit model (only for what no other tool
        can do). `code` is the BODY of a method with `app` (UIApplication),
        `uidoc` (UIDocument) and `doc` (Document) in scope and must end with a
        `return` statement (`return null;` if there is nothing to report).

        Rules:
        - Already wrapped in a committed Transaction — do NOT open your own
          (SubTransactions are fine). An exception rolls everything back.
          `transaction=False` runs the code with NO transaction (for read-only
          code, or calls Revit refuses inside one such as
          OpenAndActivateDocument / Export); then open your own if you modify.
        - Revit 2024 (.NET Framework, CodeDom): C# 5 syntax only (no `$"..."`
          interpolation, no `?.`). Revit 2025+ (.NET 8, Roslyn): modern C#.
          When unsure which Revit is running (see `ping`), stick to C# 5.
        - Usings available: System, System.Collections.Generic, System.Linq,
          Autodesk.Revit.DB(+.Architecture/.Structure), Autodesk.Revit.UI.
        - Return values are JSON-ified (Element -> summary, ElementId -> id,
          XYZ -> {x, y, z} mm, other types -> ToString())."""
        return _call("execute_code", {"code": code, "transaction": transaction})


def main() -> None:
    """Console-script entry point (see pyproject.toml). Runs the stdio server."""
    mcp.run()  # stdio transport; the MCP client launches this process


if __name__ == "__main__":
    main()
