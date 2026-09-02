<#
.SYNOPSIS
    Builds the RhinoRoad Yak package, and optionally publishes it.

.DESCRIPTION
    Stages the plugin into a framework folder, writes a manifest from the single version in
    Directory.Build.props, and runs `yak build`. Publishing is opt-in via -Push, because a push to
    the McNeel server is public and permanent: a version can be yanked, but the package name is
    claimed for good and a released version is never truly withdrawn.

    RhinoCommon is deliberately absent from the package. Rhino supplies it, and shipping a second
    copy risks two RhinoCommon assemblies loading into one process.

.EXAMPLE
    ./build-yak-package.ps1
    Builds the package only.

.EXAMPLE
    ./build-yak-package.ps1 -Push -Source https://test.yak.rhino3d.com/
    Publishes to the Yak test server, which is the safe place to rehearse a release.
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$Push,
    [string]$Source = "https://yak.rhino3d.com/",
    [string]$YakExecutable = "C:\Program Files\Rhino 8\System\Yak.exe"
)

$ErrorActionPreference = "Stop"

function Invoke-Step {
    param([string]$FilePath, [string[]]$ArgumentList)
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "Command failed: $FilePath $($ArgumentList -join ' ')" }
}

$repoRoot = $PSScriptRoot
$propsPath = Join-Path $repoRoot "Directory.Build.props"
if (-not (Test-Path $propsPath)) { throw "Version file not found at '$propsPath'." }

[xml]$props = Get-Content $propsPath
$version = $props.Project.PropertyGroup.RhinoRoadVersion
if ([string]::IsNullOrWhiteSpace($version)) { throw "RhinoRoadVersion is missing from '$propsPath'." }
if (-not (Test-Path $YakExecutable)) { throw "Yak not found at '$YakExecutable'." }

Write-Host "RhinoRoad $version ($Configuration)"

Invoke-Step "dotnet" @("build", (Join-Path $repoRoot "RhinoRoad.sln"), "-c", $Configuration)
Invoke-Step "dotnet" @("test", (Join-Path $repoRoot "RhinoRoad.sln"), "-c", $Configuration, "--nologo")

$pluginOutput = Join-Path $repoRoot "src\RhinoRoad.Rhino\bin\$Configuration"
$stageRoot = Join-Path $repoRoot ".artifacts\yak\RhinoRoad-$version"
$frameworkRoot = Join-Path $stageRoot "net8.0"
$licenseRoot = Join-Path $frameworkRoot "misc\licenses"

if (Test-Path $stageRoot) { Remove-Item -Path $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $licenseRoot -Force | Out-Null

# Ship the plugin, the assemblies Rhino cannot supply, and the runtime metadata that resolves them.
$required = @(
    "RhinoRoad.Rhino.rhp",
    "RhinoRoad.Core.dll",
    "Clipper2Lib.dll",
    "RhinoRoad.Rhino.deps.json",
    "RhinoRoad.Rhino.runtimeconfig.json"
)
foreach ($name in $required) {
    $path = Join-Path $pluginOutput $name
    if (-not (Test-Path $path)) { throw "Missing required package file '$path'. Build first." }
    Copy-Item -Path $path -Destination $frameworkRoot -Force
}

if (Test-Path (Join-Path $repoRoot "LICENSES")) {
    Copy-Item -Path (Join-Path $repoRoot "LICENSES\*") -Destination $licenseRoot -Recurse -Force
}

$rhinoCommon = Join-Path $frameworkRoot "RhinoCommon.dll"
if (Test-Path $rhinoCommon) { throw "RhinoCommon.dll must not ship inside the package." }

$manifest = @"
---
name: RhinoRoad
version: $version
authors:
- rheinason
description: >
  Rigid-vehicle access screening for Rhino 8. Drive a vehicle along a path or follow an existing
  curve, and get swept and clearance envelopes, wheel tracks and feasibility checks against the
  Danish Vejdirektoratet driving modes. Vehicle presets are certified against the official
  koerekurver drawings; each preset records whether it is reference-validated.
url: https://github.com/rheinason/RhinoRoad
keywords:
- rhinoroad
- rhino
- vehicle
- swept-path
- turning
- traffic
- road
- vejregler
"@

Set-Content -Path (Join-Path $stageRoot "manifest.yml") -Value $manifest -Encoding ascii

Push-Location $stageRoot
try {
    Invoke-Step $YakExecutable @("build", "--platform", "win")

    $package = Get-ChildItem -Path $stageRoot -Filter "*.yak" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $package) { throw "Yak build did not produce a package." }

    if ($Push) {
        Write-Host "Publishing $($package.Name) to $Source"
        Invoke-Step $YakExecutable @("push", "--source", $Source, $package.FullName)
        Write-Host "Published."
    }
    else {
        Write-Host "Package ready (not published): $($package.FullName)"
        Write-Host "Publish with: ./build-yak-package.ps1 -Push"
    }
}
finally {
    Pop-Location
}
