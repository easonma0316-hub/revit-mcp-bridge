<#
.SYNOPSIS
    Dev helper: run a command through a FRESHLY BUILT RevitMCP.Addin.dll inside
    the running Revit 2025+/2026 - without restarting Revit.

.DESCRIPTION
    Revit locks the add-in DLL it loaded at startup, so normally every C# change
    means a Revit restart. This script instead asks the *running* add-in (via
    its execute_code command) to load bin\Release\<year>\RevitMCP.Addin.dll into
    a private, collectible AssemblyLoadContext and invoke CommandRouter.Route on
    it. .NET 8 only (Revit 2025+). Needs the add-in running; REVIT_MCP_ENABLE_CODE
    is not required (that gate lives in the Python server, not the add-in).

    Caveat: the command runs inside execute_code's transaction, so commands that
    open their own Transaction must tolerate an already-open one (the CAD tools
    do: they check doc.IsModifiable).

.EXAMPLE
    .\dev\devcall.ps1 -Command list_cad_links -ParamsJson '{"include_layers":true}'
.EXAMPLE
    .\dev\devcall.ps1 -Command create_walls_from_cad -ParamsJson '{"link_id":530785,"layers":["A-WALL-S"],"level_id":311,"height_mm":3000,"dry_run":true}'
#>
param(
    [Parameter(Mandatory)][string]$Command,
    [string]$ParamsJson = '{}',
    [string]$Document = '',        # refuse to run if this is not the active document (title or path)
    [switch]$NoTransaction,        # run outside execute_code's transaction (needs add-in >= 2026-08-18; e.g. open_document)
    [int]$RevitYear = 2026,
    [string]$Url = 'http://127.0.0.1:8765/',
    [int]$TimeoutSec = 900
)
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dll = Join-Path $repo "RevitMCP.Addin\bin\Release\$RevitYear\RevitMCP.Addin.dll"
if (-not (Test-Path $dll)) { throw "Build first: $dll not found." }
if ($Document) {
    $pobj = $ParamsJson | ConvertFrom-Json
    $pobj | Add-Member -NotePropertyName expect_document -NotePropertyValue $Document -Force
    $ParamsJson = $pobj | ConvertTo-Json -Depth 20 -Compress
}
$escaped = $ParamsJson.Replace('"', '""')
$code = @"
var bytes = System.IO.File.ReadAllBytes(@"$dll");
var alc = new System.Runtime.Loader.AssemblyLoadContext("mcp-dev-" + Guid.NewGuid().ToString("N"), true);
var asm = alc.LoadFromStream(new System.IO.MemoryStream(bytes));
var router = asm.GetType("RevitMCP.Addin.CommandRouter");
var jsonT = asm.GetType("RevitMCP.Addin.Json");
var parse = jsonT.GetMethod("DeserializeObject", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
var p = (Dictionary<string, object>)parse.Invoke(null, new object[] { @"$escaped" });
var route = router.GetMethod("Route");
object result;
try { result = route.Invoke(null, new object[] { app, "$Command", p }); }
catch (System.Reflection.TargetInvocationException ex) { throw ex.InnerException ?? ex; }
var ser = jsonT.GetMethod("Serialize", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);
return (string)ser.Invoke(null, new object[] { result });
"@
$params = @{ code = $code }
if ($NoTransaction) { $params.transaction = $false }
$body = @{ command = "execute_code"; params = $params; timeout_ms = $TimeoutSec * 1000 } | ConvertTo-Json -Depth 5
# Send explicit UTF-8 bytes: PowerShell 5.1 would otherwise encode non-ASCII (e.g. Chinese layer names) in the ANSI code page.
$bytes = [Text.Encoding]::UTF8.GetBytes($body)
$r = Invoke-RestMethod $Url -Method Post -Body $bytes -ContentType "application/json; charset=utf-8" -TimeoutSec ($TimeoutSec + 5)
if (-not $r.ok) { "ERROR [$($r.code)]: $($r.error)" } else { $r.result.result | ConvertFrom-Json | ConvertTo-Json -Depth 12 }
