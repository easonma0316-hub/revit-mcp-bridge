# RevitMCP

[![build](https://github.com/easonma0316-hub/revit-mcp-bridge/actions/workflows/build.yml/badge.svg)](https://github.com/easonma0316-hub/revit-mcp-bridge/actions/workflows/build.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Revit 2024](https://img.shields.io/badge/Revit-2024-0696D7?logo=autodesk&logoColor=white)
![.NET Framework 4.8](https://img.shields.io/badge/.NET%20Framework-4.8-512BD4)
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

## Architecture

Revit runs on **.NET Framework 4.8**, where the modern C# MCP SDK can't be hosted
in-process. So RevitMCP uses the **bridge** pattern:

```
MCP client ──stdio──► mcp_server/server.py (CPython, FastMCP)
                             │  HTTP POST  {"command","params"}
                             ▼
                    RevitMCP.Addin.dll  (HttpListener inside Revit)
                             │  ExternalEvent.Raise()  +  per-request queue
                             ▼
                    Revit API on the UI thread (Transaction when writing)
```

Two Revit hard rules this design satisfies:

1. **API calls must run on Revit's UI thread** — the background HTTP thread can't
   touch the model, so every request is marshaled via an `ExternalEvent`.
2. **Model changes need a `Transaction`** — see the write commands in
   `CommandRouter.cs`.

## Project layout

| Path | Purpose |
|------|---------|
| `RevitMCP.Addin/RevitMcpApp.cs` | Add-in entry (`IExternalApplication`); fail-soft startup, port fallback |
| `RevitMCP.Addin/Config.cs` | Env-var configuration (port, read-only, confirm, timeout) |
| `RevitMCP.Addin/Log.cs` | Append-only file logger |
| `RevitMCP.Addin/McpException.cs` | Typed error with a machine-readable code |
| `RevitMCP.Addin/HttpServer.cs` | Background `HttpListener`; GET health + POST commands |
| `RevitMCP.Addin/RevitDispatcher.cs` | Marshals to the UI thread, one slot per request |
| `RevitMCP.Addin/RequestHandler.cs` | `IExternalEventHandler`; drains the request queue |
| `RevitMCP.Addin/CommandRouter.cs` | Command → Revit API action (**add tools here**) |
| `mcp_server/server.py` | FastMCP server exposing tools to the MCP client (**add tools here**) |

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
| `set_parameter` | write | Set a parameter on one or many elements (one transaction) |
| `set_selection` | ui | Select elements in the Revit UI |
| `color_elements` | write | Override element color in the active view (reversible) |
| `isolate_elements` | ui | Temporarily isolate elements in the active view |
| `reset_view` | ui | Clear temporary hide/isolate |
| `delete_elements` | write | Delete elements (confirmation dialog by default) |

All lengths and coordinates are in Revit **internal units (feet)**.

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

## Install (for users)

If you just want to *use* RevitMCP (not develop it), you don't need the .NET SDK
or to build anything.

**1. Install the Revit add-in** — download `RevitMCP-addin.zip` from the
[latest release](https://github.com/easonma0316-hub/revit-mcp-bridge/releases/latest),
unzip it, and run the bundled installer from that folder:

```powershell
.\install.ps1          # auto-detects your installed Revit year(s) and copies the add-in
```

Then start Revit, open a model, and choose **Always Load** at the security prompt.

**2. Connect the MCP server** — no clone or virtualenv needed if you have
[pipx](https://pipx.pypa.io) or [uv](https://docs.astral.sh/uv/):

```powershell
# via uv (recommended)
claude mcp add revit -- uvx --from git+https://github.com/easonma0316-hub/revit-mcp-bridge revit-mcp-bridge

# or via pipx
pipx install git+https://github.com/easonma0316-hub/revit-mcp-bridge
claude mcp add revit -- revit-mcp-bridge
```

Restart your MCP client and run `/mcp` — you should see `revit` with its tools.
(Once the package is published to PyPI, the `git+` URL becomes just
`revit-mcp-bridge`.)

Developers building from source should follow the sections below (or `SETUP.md`
for a full guided walkthrough).

## Build the add-in (from source)

Requires the .NET Framework 4.8 targeting pack (ships with Visual Studio 2022, or
install "MSBuild Tools" + the 4.8 targeting pack). The Revit 2024 API comes from
NuGet (`Nice3point.Revit.Api.*`, which repackages the real Revit assemblies), so
the project builds even on a machine without Revit installed — no `HintPath`
editing needed. To target another Revit year, bump both package versions in the
`.csproj` (e.g. `2025.*`); to use a local Revit install instead, see the comment
in the `.csproj`.

```powershell
dotnet build .\RevitMCP.Addin\RevitMCP.Addin.csproj -c Release
```

Output: `RevitMCP.Addin\bin\Release\RevitMCP.Addin.dll`. The Revit API DLLs are
compile-only and are **not** copied to `bin` — Revit loads its own at runtime.

### Continuous integration

`.github/workflows/build.yml` compiles the add-in (on Windows, since net48 is
Windows-only) and byte-compiles the Python server on every pull request and push
to `main`. It's a **compile gate**, not a functional test — it can't run tools
inside Revit.

## Install the add-in into Revit

Copy **both** the DLL and the manifest into Revit's add-ins folder (adjust the
year to match your Revit):

```powershell
$dst = "$env:APPDATA\Autodesk\Revit\Addins\2024"
Copy-Item .\RevitMCP.Addin\bin\Release\RevitMCP.Addin.dll $dst
Copy-Item .\RevitMCP.Addin\RevitMCP.addin $dst
```

Start Revit and open a model. The listener comes up on `http://127.0.0.1:8765/`.
Smoke-test it from PowerShell:

```powershell
# health (no model needed)
Invoke-RestMethod -Uri http://127.0.0.1:8765/ -Method Get

# a command
Invoke-RestMethod -Uri http://127.0.0.1:8765/ -Method Post `
  -ContentType application/json -Body '{"command":"ping"}'
```

## Set up the Python MCP server

```powershell
py -3 -m venv .venv
.\.venv\Scripts\pip install -r .\mcp_server\requirements.txt
```

## Connect to Claude Code

```powershell
claude mcp add revit -- <repo>\.venv\Scripts\python.exe <repo>\mcp_server\server.py
claude mcp list
```

Restart Claude Code, then `/mcp` should list `revit` with all the tools above.

## Adding a new tool

1. **C#** — add a `case "my_tool":` in `CommandRouter.Route` and a method that does
   the Revit work. Read tools need no transaction; wrap any model change in a
   `Transaction` and call `EnsureWritable()` first. Throw `McpException` with a
   suitable code for expected failures.
2. **Python** — add an `@mcp.tool()` function in `server.py` that calls
   `_call("my_tool", {...})`. Write a clear docstring — the LLM reads it.

Read tools reuse the existing `ExternalEvent` marshaling for free.

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
