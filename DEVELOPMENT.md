# RevitMCP — Development

How the bridge is put together, how to build and install it from source, and how to add tools. If you only want to *use* RevitMCP, the [README](README.md) install section is all you need.

## Architecture

Revit hosts add-ins in-process (**.NET Framework 4.8** up to Revit 2024, **.NET 8**
from Revit 2025), where running the C# MCP SDK alongside the Revit API is
fragile. So RevitMCP uses the **bridge** pattern:

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
| `RevitMCP.Addin/CommandRouter.Cad.cs` | CAD-link tools: layer stats, geometry export, walls from wall layers |
| `RevitMCP.Addin/CommandRouter.CadDoors.cs` | Doors from door swing arcs (hosted on the walls above) |
| `RevitMCP.Addin/CommandRouter.CadColumns.cs` | Columns from rectangles / circles on a column layer |
| `RevitMCP.Addin/Json.cs` | JSON layer (JavaScriptSerializer on net48, System.Text.Json on net8) |
| `RevitMCP.Addin/DynamicCompiler.cs` | `execute_code` compiler (CodeDom on net48, Roslyn on net8) |
| `dev/devcall.ps1` | Dev helper: run a command from a freshly built DLL inside a running Revit 2025+ (no restart) |
| `mcp_server/server.py` | FastMCP server exposing tools to the MCP client (**add tools here**) |

## Build the add-in (from source)

Requires the .NET 8 SDK (it also builds the .NET Framework 4.8 target; on a
machine without the 4.8 targeting pack install it via Visual Studio's ".NET
desktop" workload or the standalone "Developer Pack"). The Revit API comes from
NuGet (`Nice3point.Revit.Api.*`, which repackages the real Revit assemblies), so
the project builds even on a machine without Revit installed — no `HintPath`
editing needed.

The add-in is **multi-targeted**, one build per Revit runtime:

| Revit year | Runtime            | Target framework | Extra files in `bin`              |
|-----------:|--------------------|------------------|-----------------------------------|
| 2024       | .NET Framework 4.8 | `net48`          | none (single DLL)                 |
| 2025, 2026 | .NET 8             | `net8.0-windows` | `Microsoft.CodeAnalysis*.dll` (Roslyn, only used by `execute_code`) |

```powershell
# builds 2024 AND 2026
dotnet build .\RevitMCP.Addin\RevitMCP.Addin.csproj -c Release

# just one year (2025 uses the same .NET 8 code path against the 2025 API)
dotnet build .\RevitMCP.Addin\RevitMCP.Addin.csproj -c Release -p:RevitVersion=2026
dotnet build .\RevitMCP.Addin\RevitMCP.Addin.csproj -c Release -p:RevitVersion=2025
```

Output: `RevitMCP.Addin\bin\Release\<year>\` — a complete install set (the DLL,
its dependencies and `RevitMCP.addin`). The Revit API DLLs are compile-only and
are **not** copied to `bin` — Revit loads its own at runtime.

### Continuous integration

`.github/workflows/build.yml` compiles the add-in for every Revit year (on
Windows, since both targets are Windows-only) and byte-compiles the Python server
on every pull request and push to `main`. It's a **compile gate**, not a functional test — it can't run tools
inside Revit.

## Install the add-in into Revit

The easiest way is the installer, which detects installed Revit years and copies
the matching build (DLL + dependencies + manifest) for each:

```powershell
.\install.ps1                    # every installed Revit we have a build for
.\install.ps1 -RevitYears 2026   # just one
```

Or by hand — copy **everything** from the year's build folder into Revit's
add-ins folder (adjust the year to match your Revit):

```powershell
$dst = "$env:APPDATA\Autodesk\Revit\Addins\2026"
Copy-Item .\RevitMCP.Addin\bin\Release\2026\*.dll   $dst
Copy-Item .\RevitMCP.Addin\bin\Release\2026\*.addin $dst
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

Restart Claude Code, then `/mcp` should list `revit` with all the tools in the [README](README.md#tools).

## Adding a new tool

1. **C#** — add a `case "my_tool":` in `CommandRouter.Route` and a method that does
   the Revit work. Read tools need no transaction; wrap any model change in a
   `Transaction` and call `EnsureWritable()` first. Throw `McpException` with a
   suitable code for expected failures.
2. **Python** — add an `@mcp.tool()` function in `server.py` that calls
   `_call("my_tool", {...})`. Write a clear docstring — the LLM reads it.

Read tools reuse the existing `ExternalEvent` marshaling for free.

## Releasing

Pushing a version tag builds every Revit year on CI and publishes
`RevitMCP-addin.zip` (per-year folders + `install.ps1`) as a GitHub release —
see `.github/workflows/release.yml`:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

Bump `version` in `pyproject.toml` to match the tag first.
