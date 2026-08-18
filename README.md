# RevitMCP

[![build](https://github.com/easonma0316-hub/revit-mcp-bridge/actions/workflows/build.yml/badge.svg)](https://github.com/easonma0316-hub/revit-mcp-bridge/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Revit 2024-2026](https://img.shields.io/badge/Revit-2024%20%7C%202025%20%7C%202026-0696D7?logo=autodesk&logoColor=white)
![.NET Framework 4.8 / .NET 8](https://img.shields.io/badge/.NET-Framework%204.8%20%7C%208-512BD4)
![Python 3.10+](https://img.shields.io/badge/Python-3.10%2B-3776AB?logo=python&logoColor=white)

A Model Context Protocol (MCP) bridge for **Autodesk Revit**, so an MCP client
(Claude Code, Claude Desktop, Cursor, …) can read from and act on the open Revit
model — think of it as the Revit equivalent of an AI copilot wired straight into
the API.

- **Easy** — one add-in DLL + one Python process; fail-soft startup with clear
  dialogs and a log file.
- **Powerful** — a growing toolset: inspect the model, query elements, read/write
  parameters, drive selection, and highlight, isolate, or delete elements.
- **Stable** — every request is marshaled onto Revit's UI thread through a
  per-request queue, wrapped in structured error codes, with a read-only mode and
  a confirmation gate for destructive actions.

## Install

You don't need the .NET SDK or to build anything — the add-in ships prebuilt.

**Requirements:** Windows, Revit 2024 / 2025 / 2026, Python 3.10+ (or
[uv](https://docs.astral.sh/uv/) / [pipx](https://pipx.pypa.io)), and an MCP
client such as Claude Code.

**1. Install the Revit add-in** — download `RevitMCP-addin.zip` from the
[latest release](https://github.com/easonma0316-hub/revit-mcp-bridge/releases/latest),
unzip it, and run the bundled installer from that folder:

```powershell
.\install.ps1          # auto-detects your installed Revit year(s) and copies the add-in
```

(The zip holds one build per Revit year — `2024\`, `2025\`, `2026\` — and the
installer copies the matching one into `%APPDATA%\Autodesk\Revit\Addins\<year>`.
Pass `-RevitYears 2026` to pick a year explicitly.)

Then start Revit, open a model, and choose **Always Load** at the security prompt.
The add-in listens on `http://127.0.0.1:8765/` (localhost only). Quick check from
PowerShell:

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:8765/ -Method Get     # → status = alive
```

**2. Connect the MCP server** — no clone or virtualenv needed:

```powershell
# via uv (recommended)
claude mcp add revit -- uvx --from git+https://github.com/easonma0316-hub/revit-mcp-bridge revit-mcp-bridge

# or via pipx
pipx install git+https://github.com/easonma0316-hub/revit-mcp-bridge
claude mcp add revit -- revit-mcp-bridge
```

No uv / pipx? Plain `pip` works too:

```powershell
pip install git+https://github.com/easonma0316-hub/revit-mcp-bridge
claude mcp add revit -- revit-mcp-bridge            # or, if Scripts\ isn't on PATH:
claude mcp add revit -- python -m mcp_server.server
```

Restart your MCP client and run `/mcp` — you should see `revit` with its tools.
Other MCP clients (Claude Desktop, Cursor, …) use the same command
(`uvx --from git+https://github.com/easonma0316-hub/revit-mcp-bridge revit-mcp-bridge`)
in their MCP config.

**Locked-down / corporate machines:** nothing here needs admin rights or writes
outside your user profile — the add-in goes to `%APPDATA%\Autodesk\Revit\Addins\<year>`,
the log to `%LOCALAPPDATA%\RevitMCP\` (falls back to `%TEMP%`), exported images to
`%TEMP%\RevitMCP\`, and the listener binds `127.0.0.1` only (no firewall/URL-ACL
setup). If `install.ps1` is blocked by execution policy, run it with
`powershell -ExecutionPolicy Bypass -File .\install.ps1`, or just copy the year
folder's files into the Addins path by hand. If `pip` can't reach GitHub, use
`pip install mcp<2 httpx` from your internal mirror plus a source checkout
(`python <repo>\mcp_server\server.py`).

**Upgrading:** download the new zip and rerun `install.ps1` (Revit may need a
restart if it was open); the Python side updates on the next `uvx` run
(`uvx --refresh …`) or `pipx upgrade revit-mcp-bridge`.

## How it works

```
MCP client ──stdio──► revit-mcp-bridge (Python, FastMCP)
                             │  HTTP POST  {"command","params"}   (localhost)
                             ▼
                    RevitMCP.Addin.dll  (HttpListener inside Revit)
                             │  ExternalEvent  +  per-request queue
                             ▼
                    Revit API on the UI thread (Transaction when writing)
```

Every request is marshaled onto Revit's UI thread and wrapped in a transaction
when it writes, so the model is never touched from a background thread.
Details, build instructions and how to add tools: see [DEVELOPMENT.md](DEVELOPMENT.md).

## Tools

| Tool | Kind | What it does |
|------|------|--------------|
| `ping` | read | Connection + version + read-only status |
| `get_model_info` | read | Title, path, worksharing, counts of elements/views/levels |
| `list_categories` | read | Every category in the model with instance counts |
| `query_elements` | read | Find elements by category and/or name (paged with `limit`) |
| `get_element_info` | read | Full detail: type, level, location, bounding box, parameters |
| `get_parameter` | read | One parameter's value, storage type, read-only flag |
| `get_selection` | read | Elements currently selected in Revit |
| `list_views` | read | All non-template views |
| `get_active_view` | read | The active view |
| `list_levels` | read | Levels with elevations |
| `list_family_types` | read | Loadable family types (for `place_family_instance`) |
| `list_families` | read | Family definitions in the document (for `rename_element`) |
| `get_view_elements` | read | Elements visible in a view, with per-category counts |
| `export_view_image` | read | Export a view as PNG and return the file path |
| `set_parameter` | write | Set a parameter on one or many elements (one transaction) |
| `set_selection` | ui | Select elements in the Revit UI |
| `color_elements` | write | Override element color in a view (reversible via `clear`) |
| `isolate_elements` | ui | Temporarily isolate elements in the active view |
| `reset_view` | ui | Clear temporary hide/isolate |
| `set_active_view` | ui | Switch the Revit UI to another view |
| `delete_elements` | write | Delete elements (confirmation dialog by default) |
| `move_elements` | write | Move elements by a vector |
| `copy_elements` | write | Copy elements with an offset; returns the new ids |
| `create_wall` | write | Straight wall between two points on a level |
| `create_floor` | write | Floor from a closed boundary of points |
| `create_level` | write | New level at an elevation |
| `create_grid` | write | Straight grid line |
| `create_room` | write | Room at a point (0 m² result = not enclosed) |
| `place_family_instance` | write | Place a loadable family instance at a point |
| `rename_element` | write | Set an element's Name property (families, types, views, levels, …) |
| `rename_family_type` | write | Rename a FamilyManager type (family documents only) |
| `save_family_as` | write | Save the open family to a new .rfa — renames the family itself |
| `list_cad_links` | read | DWG links/imports with per-layer entity counts |
| `get_cad_geometry` | read | Raw curves of chosen CAD layers in mm (lines/arcs/polylines), bbox-filtered |
| `create_walls_from_cad` | write | Wall layer(s) → paired faces → centerlines → `Wall.Create` (dry-run first) |
| `create_doors_from_cad` | write | Door swing arcs → hosted doors with correct hand/facing, types auto-created by width |
| `create_columns_from_cad` | write | Column rectangles/circles → structural (or architectural) columns, types by size |
| `snapshot_region` | read | PNG of a plan region (bbox in mm; highlight walls/doors, hide the DWG) — look at CAD vs. modelled result |

All lengths and coordinates cross the API in **millimeters** (the add-in
converts to Revit's internal feet); parameter values use the model's **display
units** — pass 3000 to mean 3000 mm in a metric model.

### CAD-driven modelling (walls + doors from a linked DWG)

Workflow, on a floor plan whose DWG has separate wall / door layers:

1. `list_cad_links` → link id + layer statistics (spot the wall layer, e.g.
   `A-WALL-S`, and the door layer(s), e.g. `A-WINDOW`, `A-DOOR_FIRE`).
2. `get_cad_geometry` on a small `bbox_mm` to see how walls/doors are drawn.
3. `create_walls_from_cad(link_id, layers=["A-WALL-S"], level_id, height_mm,
   bbox_mm=..., door_layers=["A-WINDOW","A-DOOR_FIRE"], dry_run=True)` — check
   the planned centerlines/thicknesses/types, then rerun with `dry_run=False`.
   Every line/polyline edge becomes a segment; parallel *adjacent* segments a
   wall-thickness apart are paired (edges of one polyline preferred, so double
   walls don't become a phantom thin wall); concentric arcs become curved
   walls; collinear pieces are bridged across *door* openings (a swing arc in
   the gap) while window gaps stay open; ends are snapped to the crossing
   wall's centerline so Revit joins them. Missing thicknesses get one type each,
   duplicated from the nearest type and named by its convention
   (`SYB_WA_Generic_250mm_AI`, Type Comments = AI provenance).
4. `create_doors_from_cad(link_id, layers=["A-WINDOW"], level_id, bbox_mm=...,
   dry_run=True)` then `dry_run=False`. Swing arcs (centre = hinge, radius =
   leaf) → single / double / asymmetric doors hosted on the wall under the
   hinge; hand and facing are verified against the placed door's own plan
   swing arc, so family conventions don't matter; missing widths are rounded
   to a 100 mm grid and one type per value is duplicated from the nearest type
   (`W1200 x H2100_AI`).

   Shopfronts / tenant lines drawn as single lines (no thickness): pass them as
   `centerline_layers` → 50 mm glass placeholder walls (`SYB_WA_Glass_50mm_AI`),
   then run doors again with `host_tolerance_mm=200`.
5. `create_columns_from_cad(link_id, layers=["A-COLUMN"], level_id, height_mm)`
   for the column layer (rectangles + circles).
6. `snapshot_region(bbox_mm=..., highlight=True, hide_links=True)` → open the
   PNG and compare the modelled walls/doors/columns with the CAD; fix by hand
   or re-run with different layers / tolerances.

Work region by region (`bbox_mm`), always dry-run first, and do it on a
detached copy while tuning tolerances/type maps.

## ⚠️ Optional power tool: `execute_code` (disabled by default)

There is one more tool, **`execute_code`**, that lets the AI compile and run
**arbitrary C# inside the Revit process** — full Revit API access for anything
the curated tools can't do. It is **not registered unless you opt in**, because
arbitrary code can do far more damage than any single-purpose tool (and is not
limited to the Revit API).

**Enable it** by setting `REVIT_MCP_ENABLE_CODE=1` in the MCP server's
environment when registering it:

```powershell
claude mcp add revit --env REVIT_MCP_ENABLE_CODE=1 -- uvx --from git+https://github.com/easonma0316-hub/revit-mcp-bridge revit-mcp-bridge
```

**Disable it** by re-adding without the variable (or removing it from your MCP
config) and reconnecting — the tool then simply doesn't exist for the AI.

Notes:
- The gate lives in the Python MCP server; the add-in itself always
  understands the `execute_code` command on its localhost-only port.
- Code runs inside an auto-committed `Transaction`; an exception rolls the
  model back. It also respects `REVIT_MCP_READONLY`.
- Compiler: on Revit 2024 (.NET Framework) the in-box CodeDom compiler is C# 5
  only (no `$"..."` interpolation); on Revit 2025+ (.NET 8) Roslyn is used, so
  modern C# works.

## Configuration (environment variables)

Set these for **Revit's process** (they configure the add-in). The port/URL also
has a matching variable on the Python side.

| Variable | Default | Effect |
|----------|---------|--------|
| `REVIT_MCP_PORT` | `8765` | Preferred listener port. If taken, the add-in probes the next 10 ports and tells you which it bound. |
| `REVIT_MCP_READONLY` | `0` | `1` blocks every model-changing command (`READ_ONLY` error). |
| `REVIT_MCP_CONFIRM` | `1` | `0` skips the Revit confirmation dialog before deletes. |
| `REVIT_MCP_TIMEOUT_MS` | `60000` | Max time a single command may run on the UI thread. |
| `REVIT_MCP_URL` | `http://127.0.0.1:8765/` | *(Python side)* add-in URL; set this if Revit bound a fallback port. |
| `REVIT_MCP_TIMEOUT` | `65` | *(Python side)* HTTP timeout in seconds. |
| `REVIT_MCP_ENABLE_CODE` | `0` | *(Python side)* `1` registers the `execute_code` tool (see the warning section above). |

## Troubleshooting

- **Client can't reach Revit (`NOT_CONNECTED`)** — is Revit open with a model
  loaded and the add-in installed? Check the log (below). If a fallback port was
  used, set `REVIT_MCP_URL` to match.
- **Requests time out (`TIMEOUT`)** — a modal dialog open in Revit blocks the UI
  thread; close it. Long operations may need a bigger `REVIT_MCP_TIMEOUT_MS`.
- **`READ_ONLY`** — the add-in was started with `REVIT_MCP_READONLY=1`.
- **Log file** — `%LOCALAPPDATA%\RevitMCP\RevitMCP.log` records startup, every
  command, and errors. Start here when debugging.

## Notes / gotchas

- Keep Revit open with a document active; most commands need `ActiveUIDocument`.
- Requests are serialized on the UI thread — fine for interactive use.
- Destructive `delete_elements` prompts in Revit unless `REVIT_MCP_CONFIRM=0`.
- The listener binds to `127.0.0.1` only, so it is never exposed off the machine.

## Development

Building from source, project layout, adding new tools, CI and releasing are
covered in [DEVELOPMENT.md](DEVELOPMENT.md); [SETUP.md](SETUP.md) is a
step-by-step install-and-test playbook you can hand to an AI agent.
