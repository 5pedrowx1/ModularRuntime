[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pluginsRoot = Join-Path $repositoryRoot 'plugins'
# Every sample project with a plugin.json is a plugin; EmbeddedExample is a host and has none.
$samples = Get-ChildItem -Path (Join-Path $repositoryRoot 'samples') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'plugin.json') } |
    Sort-Object Name |
    ForEach-Object { $_.Name }

New-Item -ItemType Directory -Path $pluginsRoot -Force | Out-Null

foreach ($sample in $samples) {
    $projectPath = Join-Path $repositoryRoot "samples/$sample/$sample.csproj"
    $manifestPath = Join-Path $repositoryRoot "samples/$sample/plugin.json"
    $outputPath = Join-Path $repositoryRoot "samples/$sample/bin/$Configuration/net10.0"

    Write-Host "Building $sample ($Configuration)..."
    dotnet build $projectPath -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Building '$sample' failed with exit code $LASTEXITCODE."
    }

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($manifest.id)) {
        throw "Manifest '$manifestPath' does not declare an id."
    }

    $destination = Join-Path $pluginsRoot $manifest.id
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }

    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -Path (Join-Path $outputPath '*') -Destination $destination -Recurse -Force
    Write-Host "  -> $destination"
}

Write-Host ''
Write-Host "Plugins prepared under '$pluginsRoot' for $Configuration."
