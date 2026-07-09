# RevitMCP — Setup & Test Playbook

This is a step-by-step runbook for installing and testing RevitMCP on a Windows
machine with Revit 2024. It is written so you can hand it to an AI coding agent
(e.g. Claude Code in your terminal) and say:

> "Follow SETUP.md step by step. Run one phase at a time, verify the checkpoint
> before moving on, and stop and tell me if a checkpoint fails."

**Core principle: test from the inside out.** Each phase proves one layer works
before you build the next on top of it. If something breaks, you know exactly
which layer to look at.

> ⚠️ Must run on a **Windows** machine with **Revit 2024** installed. The Revit
> add-in cannot run on Linux/macOS.

---

## Phase 0 — Prerequisites (check, don't assume)

Run these and confirm each is present:

```powershell
dotnet --version        # need a .NET SDK (includes the tooling to build net48)
python --version        # need Python 3.10+
Test-Path "C:\Program Files\Autodesk\Revit 2024\Revit.exe"   # should be True
```

- ✅ **Checkpoint:** `dotnet` prints a version, `python` prints 3.10+, and the
  Revit path test returns `True`.
- ❌ **If it fails:** install the missing piece.
  - No `dotnet` → install the .NET SDK (or Visual Studio 2022 with the ".NET
    desktop" workload, which includes the .NET Framework 4.8 targeting pack).
  - No `python` → install Python 3.10+ and re-open the terminal.
  - Revit not at that path → note your actual install path; you may need to edit
    the Revit year folder in later steps.

---

## Phase 1 — Build and install the add-in

From the repository root:

```powershell
# 1. Build the add-in
dotnet build .\RevitMCP.Addin\RevitMCP.Addin.csproj -c Release

# 2. Copy the DLL + manifest into Revit's add-ins folder
$dst = "$env:APPDATA\Autodesk\Revit\Addins\2024"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item .\RevitMCP.Addin\bin\Release\RevitMCP.Addin.dll $dst -Force
Copy-Item .\RevitMCP.Addin\RevitMCP.addin $dst -Force

# 3. Confirm both files landed
Get-ChildItem $dst\RevitMCP.*
```

- ✅ **Checkpoint:** the build reports **Build succeeded**, and the last command
  lists both `RevitMCP.Addin.dll` and `RevitMCP.addin`.
- ❌ **If it fails:**
  - Build error → read the first error line. A NuGet restore issue means no
    internet; a Revit-API error means the package version doesn't match your
    Revit (see `.csproj` comments).
  - Copy error → the Addins folder path is wrong for your Revit version; adjust
    the `2024` in the path.

Now **start Revit and open any model**. On first launch Revit shows a security
prompt for the unsigned add-in — choose **Always Load**.

---

## Phase 2 — Test the add-in directly ⭐ (the most important checkpoint)

Do **not** involve Python or Claude yet. Talk to the add-in straight from
PowerShell. If this works, the hard part (the Revit side) is proven.

```powershell
# Health probe (GET) — works even with no model open
Invoke-RestMethod -Uri http://127.0.0.1:8765/ -Method Get

# ping (POST)
Invoke-RestMethod -Uri http://127.0.0.1:8765/ -Method Post `
  -ContentType application/json -Body '{"command":"ping"}'

# a real read
Invoke-RestMethod -Uri http://127.0.0.1:8765/ -Method Post `
  -ContentType application/json -Body '{"command":"get_model_info"}'
```

- ✅ **Checkpoint:** the health probe returns `status = alive`; `ping` returns
  `service = RevitMCP` with a Revit version and the open document's title;
  `get_model_info` returns element/view/level counts.
- ❌ **If it fails:**
  - Connection refused → is Revit running with a model open? Is the add-in
    loaded? Check the log: `Get-Content $env:LOCALAPPDATA\RevitMCP\RevitMCP.log -Tail 40`.
  - Startup dialog said it used a **different port** → use that port for the rest
    of this playbook, and remember it for Phase 4 (`REVIT_MCP_URL`).

> The log at `%LOCALAPPDATA%\RevitMCP\RevitMCP.log` records startup, every
> command, and errors. Read it whenever anything is unclear.

---

## Phase 3 — Set up the Python MCP server

```powershell
py -3 -m venv .venv
.\.venv\Scripts\pip install -r .\mcp_server\requirements.txt
```

- ✅ **Checkpoint:** pip finishes without errors and `.venv\Scripts\python.exe`
  exists.
- ❌ **If it fails:** ensure `py -3` works (Python launcher installed); otherwise
  use the full path to your `python.exe` to create the venv.

---

## Phase 4 — Connect to Claude Code

Use **absolute paths**. Replace `<REPO>` with the repo's full path (run `$PWD`
to get it).

```powershell
claude mcp add revit -- <REPO>\.venv\Scripts\python.exe <REPO>\mcp_server\server.py
claude mcp list
```

If Phase 2 showed a non-default port, pass it through:

```powershell
claude mcp add revit --env REVIT_MCP_URL=http://127.0.0.1:<PORT>/ -- <REPO>\.venv\Scripts\python.exe <REPO>\mcp_server\server.py
```

Then **restart Claude Code** and run `/mcp`.

- ✅ **Checkpoint:** `/mcp` lists `revit` with 16 tools (ping, get_model_info,
  list_categories, query_elements, get_element_info, get_parameter,
  get_selection, list_views, get_active_view, list_levels, set_parameter,
  set_selection, color_elements, isolate_elements, reset_view, delete_elements).
- ❌ **If it fails:** re-check the two absolute paths; run the `server.py` path
  with the venv python manually to see startup errors.

---

## Phase 5 — Functional test through Claude (safe → risky order)

With Revit open on a model, ask Claude these in order. Each exercises a layer;
the order goes from read-only to reversible to destructive.

1. **Connect** — "Use the revit ping tool to confirm the connection."
2. **Overview** — "Show me the Revit model info."
3. **Discover** — "List the categories in the model."
4. **Query** — "Query up to 10 walls." *(note one returned id)*
5. **Detail** — "Get the full info for element `<id>`."
6. **Selection sync** — select a few elements in Revit by hand, then ask
   "What's currently selected in Revit?"
7. **Visual (reversible)** — "Color elements `[<id1>, <id2>]` red", then
   "Isolate those elements", then "Reset the view."
8. **Write (changes the model)** — "Set the Comments parameter to 'MCP test' on
   element `<id>`", then verify the value changed in Revit's Properties panel.
9. **Delete (destructive, test last)** — "Delete element `<id>`." Revit pops a
   confirmation dialog; click **Yes** to delete, or **No** to see the tool
   return `CANCELLED`.

- ✅ **Checkpoint:** reads return data, color/isolate visibly change the active
  view and reset cleanly, the parameter write shows up in Revit, and delete is
  gated by the confirmation dialog.
- ❌ **If it fails:** the error carries a code — see the troubleshooting table.

---

## Optional — verify the safety features

- **Read-only mode:** close Revit, set `REVIT_MCP_READONLY=1` (e.g.
  `setx REVIT_MCP_READONLY 1`, then open a **new** terminal and restart Revit),
  and ask Claude to change a parameter → it should return `READ_ONLY`. Remove the
  variable afterwards (`setx REVIT_MCP_READONLY ""`) and restart Revit.
- **Timeout guard:** open any modal dialog in Revit (e.g. Options) and leave it
  open, then call a tool → it should return `TIMEOUT` instead of hanging.

---

## Troubleshooting quick reference

| Symptom / error code | Check |
|----------------------|-------|
| `NOT_CONNECTED` | Revit running? Model open? Add-in loaded? Right port? |
| `TIMEOUT` | A modal dialog is open in Revit and blocking the UI thread — close it. |
| `READ_ONLY` | `REVIT_MCP_READONLY=1` is set; unset it to allow writes. |
| `NO_DOCUMENT` | Open a model in Revit first. |
| `NOT_FOUND` | The element id / parameter name doesn't exist — re-query. |
| Tool behaves oddly | Read the log: `%LOCALAPPDATA%\RevitMCP\RevitMCP.log`. |

## Configuration knobs (environment variables)

Set on **Revit's process** for the add-in, or via `--env` on `claude mcp add`
for the Python side. Full list is in the README's Configuration section:
`REVIT_MCP_PORT`, `REVIT_MCP_READONLY`, `REVIT_MCP_CONFIRM`,
`REVIT_MCP_TIMEOUT_MS` (add-in) and `REVIT_MCP_URL`, `REVIT_MCP_TIMEOUT` (Python).
