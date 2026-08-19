<#
.SYNOPSIS
    Installs the RevitMCP add-in into Revit's Addins folder.

.DESCRIPTION
    Copies the add-in files (RevitMCP.Addin.dll, its dependencies and
    RevitMCP.addin) into %APPDATA%\Autodesk\Revit\Addins\<year>.

    Each Revit year needs its own build (2024 = .NET Framework 4.8,
    2025/2026 = .NET 8), so files are looked up per year in:
      <script folder>\<year>\                        (extracted release zip)
      RevitMCP.Addin\bin\Release\<year>\             (source checkout after
                                                       `dotnet build -c Release`)
      <script folder>                                (flat legacy zip)

.PARAMETER RevitYears
    One or more Revit years to install into, e.g. -RevitYears 2024,2026.
    Default: every installed Revit (C:\Program Files\Autodesk\Revit <year>,
    2024 or newer) that we have a build for.

.PARAMETER Source
    Folder containing the add-in files for ONE year (RevitMCP.Addin.dll +
    RevitMCP.addin). Overrides auto-detection; use with a single -RevitYears.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -RevitYears 2026
.EXAMPLE
    .\install.ps1 -RevitYears 2025 -Source .\RevitMCP.Addin\bin\Release\2025
#>
[CmdletBinding()]
param(
    [int[]]$RevitYears,
    [string]$Source
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins"

function Test-AddinFolder([string]$Folder) {
    return (Test-Path (Join-Path $Folder "RevitMCP.Addin.dll")) -and
           (Test-Path (Join-Path $Folder "RevitMCP.addin"))
}

function Find-Source([int]$Year, [string]$Override, [string]$ScriptDir) {
    $candidates = @()
    if ($Override) { $candidates += $Override }
    $candidates += (Join-Path $ScriptDir "$Year")                                   # release zip
    $candidates += (Join-Path $ScriptDir "RevitMCP.Addin\bin\Release\$Year")        # source checkout
    $candidates += $ScriptDir                                                        # flat legacy zip
    foreach ($c in $candidates) {
        if (Test-AddinFolder $c) { return (Resolve-Path $c).Path }
    }
    return $null
}

# ---- which years? -----------------------------------------------------------
if (-not $RevitYears -or $RevitYears.Count -eq 0) {
    $RevitYears = @(Get-ChildItem "C:\Program Files\Autodesk" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^Revit (\d{4})$' -and (Test-Path (Join-Path $_.FullName "Revit.exe")) } |
        ForEach-Object { [int]($_.Name -replace 'Revit ', '') })
    if ($RevitYears.Count -eq 0) {
        Write-Host "No Revit install detected under C:\Program Files\Autodesk; defaulting to 2026." -ForegroundColor Yellow
        $RevitYears = @(2026)
    }
}
if ($Source -and $RevitYears.Count -gt 1) {
    throw "-Source points at a single-year build; combine it with exactly one -RevitYears value."
}

# ---- install ----------------------------------------------------------------
$minYear   = 2024   # older Revit APIs lack ElementId.Value / ForgeTypeId units the add-in relies on
$installed = @()
$skipped   = @()
foreach ($year in ($RevitYears | Sort-Object -Unique)) {
    if ($year -lt $minYear) {
        Write-Host "Skipping Revit $year (unsupported; RevitMCP needs Revit $minYear or newer)." -ForegroundColor DarkGray
        continue
    }
    $src = Find-Source -Year $year -Override $Source -ScriptDir $scriptDir
    if (-not $src) { $skipped += $year; continue }

    $dst = Join-Path $addinsRoot "$year"
    New-Item -ItemType Directory -Force -Path $dst | Out-Null

    # Files extracted from a downloaded zip carry the "Mark of the Web"; .NET then refuses to
    # load the DLL and Revit shows "Revit cannot run the external application RevitMCP".
    Get-ChildItem $src -File | Unblock-File -ErrorAction SilentlyContinue

    # Everything the build produced for this year: the add-in DLL, its
    # dependencies (Roslyn on .NET 8) and the manifest.
    foreach ($file in (Get-ChildItem $src -File | Where-Object { $_.Extension -in '.dll', '.addin' })) {
        $target = Join-Path $dst $file.Name
        try {
            Copy-Item $file.FullName $target -Force
        }
        catch {
            # Revit is running and has the old DLL locked: park it under .old,
            # drop the new one in place; Revit picks it up on next start.
            $old = "$target.old"
            if (Test-Path $old) { Remove-Item $old -Force -ErrorAction SilentlyContinue }
            if (Test-Path $old) {
                # The previous .old is itself still loaded by Revit; park under a fresh name.
                $old = "$target.old." + [guid]::NewGuid().ToString('N').Substring(0, 8)
            }
            Rename-Item $target $old -Force
            Copy-Item $file.FullName $target -Force
            Write-Host "  $($file.Name) was locked (Revit running?) - replaced; restart Revit $year to load it." -ForegroundColor Yellow
        }
    }
    Get-ChildItem $dst -File | Unblock-File -ErrorAction SilentlyContinue
    # Clean up parked copies from earlier installs when nothing holds them any more.
    Get-ChildItem $dst -Filter "*.old*" -File -ErrorAction SilentlyContinue |
        ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

    Write-Host "Installed RevitMCP into Revit $year  <-  $src" -ForegroundColor Green
    $installed += $year
}

if ($skipped.Count -gt 0) {
    Write-Host ""
    Write-Host ("No build found for Revit {0}. Build it first:" -f ($skipped -join ", ")) -ForegroundColor Yellow
    foreach ($y in $skipped) {
        Write-Host "  dotnet build .\RevitMCP.Addin\RevitMCP.Addin.csproj -c Release -p:RevitVersion=$y"
    }
    Write-Host '  (a plain "dotnet build -c Release" produces 2024 and 2026), or pass -Source <folder>.'
}
if ($installed.Count -eq 0) { throw "Nothing installed." }

Write-Host ""
Write-Host "Done. Next steps:" -ForegroundColor Cyan
Write-Host "  1. Start Revit and open a model (choose 'Always Load' at the security prompt)."
Write-Host "  2. Verify the listener:  Invoke-RestMethod http://127.0.0.1:8765/ -Method Get"
Write-Host "  3. Connect your MCP client (see the README 'Install (for users)' section)."
