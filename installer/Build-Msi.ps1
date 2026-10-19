<#
.SYNOPSIS
    Builds the complete self-contained SQL DAM release and packages it into an MSI installer.
.DESCRIPTION
    1. Runs dotnet publish for win-x64 self-contained.
    2. Runs GenerateFiles.ps1 to harvest published files into WiX format.
    3. Runs wix build to produce the final .msi installer.
#>

[CmdletBinding()]
param (
    [string]$Version = "1.0.0.0"
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
$publishDir = Join-Path $scriptDir "publish"
$projectFile = Join-Path $projectRoot "src\SqlDam.Web\SqlDam.Web.csproj"
$desktopProjectFile = Join-Path $projectRoot "src\SqlDam.Desktop\SqlDam.Desktop.csproj"
$msiOutput = Join-Path $scriptDir "SqlDam-Setup-v1.0.0.msi"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host " Building SQL DAM MSI Installer v$Version " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Stop any running instances
Get-Process SqlDam* -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 2. Publish self-contained
Write-Host "`n[1/3] Publishing self-contained win-x64 release (Web Service & Desktop Client)..." -ForegroundColor Yellow
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}
dotnet publish $projectFile -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Web service publish failed with exit code $LASTEXITCODE." }
dotnet publish $desktopProjectFile -c Release -r win-x64 --self-contained -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Desktop application publish failed with exit code $LASTEXITCODE." }

if (-not (Test-Path (Join-Path $publishDir "SqlDam.Web.exe"))) {
    throw "Publish failed: SqlDam.Web.exe was not created."
}
if (-not (Test-Path (Join-Path $publishDir "SqlDam.Desktop.exe"))) {
    throw "Publish failed: SqlDam.Desktop.exe was not created."
}

# 3. Generate WiX file manifest
Write-Host "`n[2/3] Generating WiX component manifest..." -ForegroundColor Yellow
powershell -ExecutionPolicy Bypass -File (Join-Path $scriptDir "GenerateFiles.ps1")
if ($LASTEXITCODE -ne 0) { throw "WiX file manifest generation failed with exit code $LASTEXITCODE." }

# 4. Build MSI with WiX v4
Write-Host "`n[3/3] Compiling MSI package with WiX Toolset..." -ForegroundColor Yellow
Push-Location $scriptDir
try {
    wix build Package.wxs Files.wxs SetupWizard.wxs -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -arch x64 -o $msiOutput
    if ($LASTEXITCODE -ne 0) { throw "WiX MSI build failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

if (Test-Path $msiOutput) {
    $item = Get-Item $msiOutput
    $sizeMb = [Math]::Round($item.Length / 1MB, 2)
    Write-Host "`n========================================================" -ForegroundColor Green
    Write-Host " SUCCESS: MSI installer created successfully!" -ForegroundColor Green
    Write-Host " File: $msiOutput ($sizeMb MB)" -ForegroundColor Green
    Write-Host "========================================================" -ForegroundColor Green
} else {
    throw "MSI build failed: output file not found."
}
