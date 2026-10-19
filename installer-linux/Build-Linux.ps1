[CmdletBinding()]
param (
    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$Runtime = 'linux-x64'
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $scriptDir '..\src\SqlDam.Web\SqlDam.Web.csproj'
$stagingDir = Join-Path $scriptDir ".staging-$Runtime"
$packageDir = Join-Path $stagingDir "SQL-DAM-$Runtime"
$appDir = Join-Path $packageDir 'app'
$distDir = Join-Path $scriptDir 'dist'
$archive = Join-Path $distDir "SQL-DAM-$Runtime.tar.gz"

$resolvedScriptDir = [System.IO.Path]::GetFullPath($scriptDir).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$resolvedStagingDir = [System.IO.Path]::GetFullPath($stagingDir)
if (-not $resolvedStagingDir.StartsWith($resolvedScriptDir, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Staging directory escaped the Linux installer workspace: $resolvedStagingDir"
}

if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path $distDir -Force | Out-Null

Write-Host "Publishing SQL DAM for $Runtime..." -ForegroundColor Cyan
dotnet publish $projectFile -c Release -r $Runtime --self-contained true -o $appDir
if ($LASTEXITCODE -ne 0) {
    throw "Linux publish failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $scriptDir 'install.sh') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $scriptDir 'sqldam.service') -Destination $packageDir
Copy-Item -LiteralPath (Join-Path $scriptDir 'README.md') -Destination $packageDir

if (-not (Test-Path -LiteralPath (Join-Path $appDir 'SqlDam.Web'))) {
    throw "Publish failed: the Linux SqlDam.Web executable was not created."
}

if (Test-Path -LiteralPath $archive) {
    Remove-Item -LiteralPath $archive -Force
}
Push-Location $stagingDir
try {
    tar.exe -czf $archive "SQL-DAM-$Runtime"
    if ($LASTEXITCODE -ne 0) {
        throw "Linux installer archive creation failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$item = Get-Item -LiteralPath $archive
$sizeMb = [Math]::Round($item.Length / 1MB, 2)
Write-Host "Linux installer created: $archive ($sizeMb MB)" -ForegroundColor Green
