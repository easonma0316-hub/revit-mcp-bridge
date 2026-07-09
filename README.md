# RevitMCP

A Model Context Protocol (MCP) bridge for **Revit 2024**, so Claude (Code or
Desktop) can read from and act on the open Revit model — the Revit equivalent of
RhinoAIMCP.

## Architecture

Revit 2024 runs on **.NET Framework 4.8**, where the official C# MCP SDK (modern
.NET only) can't be hosted in-process. So this uses the **bridge** pattern:

```
Claude Code ──stdio──► mcp_server/server.py (CPython, FastMCP)
                                │  HTTP POST  {"command","params"}
                                ▼
                       RevitMCP.Addin.dll  (HttpListener inside Revit)
                                │  ExternalEvent.Raise()  +  ManualResetEvent
                                ▼
                       Revit API on the UI thread (Transaction)
```

The two Revit hard rules this design exists to satisfy:
1. **API calls must run on Revit's UI thread** — the background HTTP thread can't
   touch the model, so every request is marshaled via an `ExternalEvent`.
2. **Model changes need a `Transaction`** — see `SetParameter` in `CommandRouter.cs`.

## Project layout

| Path | Purpose |
|------|---------|
| `RevitMCP.Addin/RevitMcpApp.cs` | Add-in entry (`IExternalApplication`); starts the HTTP server |
| `RevitMCP.Addin/HttpServer.cs` | Background `HttpListener`, JSON in/out |
| `RevitMCP.Addin/RevitDispatcher.cs` | Marshals to UI thread, blocks for the result |
| `RevitMCP.Addin/RequestHandler.cs` | The `IExternalEventHandler` that runs on the UI thread |
| `RevitMCP.Addin/CommandRouter.cs` | Command → Revit API action (**add tools here**) |
| `RevitMCP.Addin/RevitMCP.addin` | Add-in manifest |
| `mcp_server/server.py` | FastMCP server exposing tools to Claude (**add tools here**) |

## Build the add-in

Requires the .NET Framework 4.8 targeting pack (ships with Visual Studio 2022, or
install "MSBuild Tools" + the 4.8 targeting pack).

```powershell
dotnet build "D:\GitHub\RevitMCP\RevitMCP.Addin\RevitMCP.Addin.csproj" -c Release
```

Output: `RevitMCP.Addin\bin\Release\RevitMCP.Addin.dll`.

## Install the add-in into Revit

Copy **both** the DLL and the manifest into Revit's add-ins folder:

```powershell
$dst = "$env:APPDATA\Autodesk\Revit\Addins\2024"
Copy-Item "D:\GitHub\RevitMCP\RevitMCP.Addin\bin\Release\RevitMCP.Addin.dll" $dst
Copy-Item "D:\GitHub\RevitMCP\RevitMCP.Addin\RevitMCP.addin" $dst
```

Start Revit. The listener comes up on `http://127.0.0.1:8765/`.
Smoke-test it (with a document open) from PowerShell:

```powershell
Invoke-RestMethod -Uri http://127.0.0.1:8765/ -Method Post `
  -ContentType application/json -Body '{"command":"ping"}'
```

## Set up the Python MCP server

```powershell
py -3 -m venv D:\GitHub\RevitMCP\.venv
D:\GitHub\RevitMCP\.venv\Scripts\pip install -r D:\GitHub\RevitMCP\mcp_server\requirements.txt
```

## Connect to Claude Code

```powershell
claude mcp add revit -- D:\GitHub\RevitMCP\.venv\Scripts\python.exe D:\GitHub\RevitMCP\mcp_server\server.py
claude mcp list
```

Restart Claude Code, then `/mcp` should list `revit` with 26 tools:

| Group | Tools |
|---|---|
| Connection | `ping` |
| Project & model queries | `get_project_info`, `list_levels`, `list_views`, `list_categories`, `list_family_types` |
| Element queries | `get_elements`, `filter_elements`, `get_view_elements`, `get_selection`, `get_element_info`, `get_location` |
| Element edits | `set_parameter`, `move_elements`, `copy_elements`, `delete_elements` |
| Creation | `create_wall`, `create_floor`, `create_level`, `create_grid`, `create_room`, `place_family_instance` |
| UI & views | `select_elements`, `set_active_view`, `color_elements`, `export_view_image` |

## ⚠️ Optional power tool: `execute_code` (disabled by default)

There is a 27th tool, **`execute_code`**, that lets the AI compile and run
**arbitrary C# inside the Revit process** — full Revit API access for anything
the curated tools can't do. It is **not registered unless you opt in**, because
arbitrary code can do far more damage than any single-purpose tool (and is not
limited to the Revit API).

**Enable it** by setting `REVIT_MCP_ENABLE_CODE=1` in the MCP server's
environment when registering it:

```powershell
claude mcp add revit --env REVIT_MCP_ENABLE_CODE=1 -- D:\GitHub\RevitMCP\.venv\Scripts\python.exe D:\GitHub\RevitMCP\mcp_server\server.py
```

**Disable it** by re-adding without the variable (or removing it from your MCP
config) and reconnecting — the tool then simply doesn't exist for the AI.

Notes:
- The gate lives in the MCP server (`server.py`); the add-in itself always
  understands the `execute_code` command on its localhost-only port.
- Code runs inside an auto-committed `Transaction`; an exception rolls the
  model back.
- The in-process CodeDom compiler is C# 5 only (no `$"..."` interpolation).

## Adding a new tool

1. **C#** — add a `case "my_tool":` in `CommandRouter.Route` and a method that does
   the Revit work (wrap any model change in a `Transaction`).
2. **Python** — add an `@mcp.tool()` function in `server.py` that calls
   `_call("my_tool", {...})`.

That's the whole loop — read tools need no transaction; write tools reuse the
existing `ExternalEvent` marshaling for free.

## Notes / gotchas

- Keep Revit open with a document active; many commands need `ActiveUIDocument`.
- Requests are serialized (one `ExternalEvent` at a time) — fine for an MVP.
- A modal dialog open in Revit will block the UI thread and time out requests.
- Port `8765` is set in `RevitMcpApp.cs`; if you change it, set `REVIT_MCP_URL`
  for the Python process to match.
