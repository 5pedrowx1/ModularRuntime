<#
.SYNOPSIS
    Packs the public ModularRuntime SDK packages into a local folder (default: artifacts\nuget).

.DESCRIPTION
    Packs ModularRuntime.Contracts, ModularRuntime.Sdk, ModularRuntime.Engine, ModularRuntime.Loader,
    ModularRuntime.Templates (dotnet new templates), and ModularRuntime.DevTools (the
    "dotnet modularruntime" tool). Nothing is published: use the folder as a local NuGet feed.

.EXAMPLE
    ./scripts/pack-sdk.ps1
    dotnet new install ./artifacts/nuget/ModularRuntime.Templates.0.6.0.nupkg
    dotnet tool install --global ModularRuntime.DevTools --add-source ./artifacts/nuget
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [string] $Output
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'artifacts\nuget' }
New-Item -ItemType Directory -Force $Output | Out-Null

$projects = @(
    'src\ModularRuntime.Contracts\ModularRuntime.Contracts.csproj',
    'src\ModularRuntime.Sdk\ModularRuntime.Sdk.csproj',
    'src\ModularRuntime.Engine\ModularRuntime.Engine.csproj',
    'src\ModularRuntime.Loader\ModularRuntime.Loader.csproj',
    'src\ModularRuntime.Templates\ModularRuntime.Templates.csproj',
    'src\ModularRuntime.DevTools\ModularRuntime.DevTools.csproj'
)

foreach ($project in $projects) {
    dotnet pack (Join-Path $root $project) -c $Configuration -o $Output --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $project" }
}

Write-Host ""
Write-Host "Local packages in ${Output}:"
Get-ChildItem $Output -Filter *.nupkg | Sort-Object Name | ForEach-Object { Write-Host "  $($_.Name)" }
