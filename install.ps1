<#
.SYNOPSIS
    Installs the RevitMCP add-in into Revit's Addins folder.

.DESCRIPTION
    Copies RevitMCP.Addin.dll + RevitMCP.addin into
    %APPDATA%\Autodesk\Revit\Addins\<year>. Works both from an extracted
    release zip (files sit next to this script) and from a source checkout
    (files under RevitMCP.Addin\bin\Release after `dotnet build -c Release`).

.PARAMETER RevitYears
    One or more Revit years to install into, e.g. -RevitYears 2024,2025.
    Default: auto-detect installed years, falling back to 2024.

.PARAMETER Source
    Folder containing RevitMCP.Addin.dll + RevitMCP.addin. Default: auto-detect.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -RevitYears 2024,2025
#>
[CmdletBinding()]
param(
    [int[]]$RevitYears,
    [string]$Source
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-Source {
    param([string]$Source, [string]$ScriptDir)
    $candidates = @()
    if ($Source) { $candidates += $Source }
    $candidates += $ScriptDir                                                   # extracted release
    $candidates += (Join-Path $ScriptDir "RevitMCP.Addin\bin\Release")          # source checkout
    foreach ($c in $candidates) {
        if ((Test-Path (Join-Path $c "RevitMCP.Addin.dll")) -and
            (Test-Path (Join-Path $c "RevitMCP.addin"))) {
            return (Resolve-Path $c).Path
        }
    }
    throw ("Could not find RevitMCP.Addin.dll + RevitMCP.addin. " +
           "Build first (dotnet build .\RevitMCP.Addin\RevitMCP.Addin.csproj -c Release), " +
           "run this from the extracted release folder, or pass -Source <folder>.")
}

$src = Find-Source -Source $Source -ScriptDir $scriptDir
Write-Host "Using add-in files from: $src"

$addinsRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins"

if (-not $RevitYears -or $RevitYears.Count -eq 0) {
    if (Test-Path $addinsRoot) {
        $RevitYears = @(Get-ChildItem $addinsRoot -Directory |
            Where-Object { $_.Name -match '^\d{4}$' } |
            ForEach-Object { [int]$_.Name })
    }
    if (-not $RevitYears -or $RevitYears.Count -eq 0) {
        Write-Host "No installed Revit years detected; defaulting to 2024." -ForegroundColor Yellow
        $RevitYears = @(2024)
    }
}

foreach ($year in $RevitYears) {
    $dst = Join-Path $addinsRoot "$year"
    New-Item -ItemType Directory -Force -Path $dst | Out-Null
    Copy-Item (Join-Path $src "RevitMCP.Addin.dll") $dst -Force
    Copy-Item (Join-Path $src "RevitMCP.addin")     $dst -Force
    Write-Host "Installed RevitMCP into Revit $year  ->  $dst" -ForegroundColor Green
}

Write-Host ""
Write-Host "Done. Next steps:" -ForegroundColor Cyan
Write-Host "  1. Start Revit and open a model (choose 'Always Load' at the security prompt)."
Write-Host "  2. Verify the listener:  Invoke-RestMethod http://127.0.0.1:8765/ -Method Get"
Write-Host "  3. Connect your MCP client (see the README 'Install (for users)' section)."
