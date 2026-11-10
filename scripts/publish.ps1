<#
.SYNOPSIS
    Publishes ModularRuntime.Daemon, ModularRuntime.Cli and ModularRuntime.Desktop into one folder.

.DESCRIPTION
    The three executables must sit side by side: the CLI and the desktop app locate the daemon next
    to themselves (or through MODULARRUNTIME_DAEMON_PATH). The output is framework-dependent by
    default (requires the .NET 10 Desktop Runtime); use -SelfContained to bundle the runtime.

.EXAMPLE
    ./scripts/publish.ps1
    ./scripts/publish.ps1 -SelfContained -Output C:\Tools\ModularRuntime
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [string] $Runtime = 'win-x64',

    [string] $Output,

    [switch] $SelfContained
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) {
    $Output = Join-Path $root "artifacts\publish\$Runtime"
}

$Output = [System.IO.Path]::GetFullPath($Output)
if (Test-Path $Output) {
    Remove-Item -Recurse -Force $Output
}

New-Item -ItemType Directory -Force $Output | Out-Null

$projects = @(
    'src\ModularRuntime.Daemon\ModularRuntime.Daemon.csproj',
    'src\ModularRuntime.Cli\ModularRuntime.Cli.csproj',
    'src\ModularRuntime.Desktop\ModularRuntime.Desktop.csproj'
)

foreach ($project in $projects) {
    Write-Host "Publishing $project ..."
    $arguments = @(
        'publish', (Join-Path $root $project),
        '-c', $Configuration,
        '-r', $Runtime,
        '-o', $Output,
        "--self-contained:$($SelfContained.IsPresent.ToString().ToLowerInvariant())",
        '-p:PublishReadyToRun=false',
        '-nologo', '-v', 'q'
    )
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $project (exit code $LASTEXITCODE)."
    }
}

$expected = 'ModularRuntime.Daemon.exe', 'ModularRuntime.Cli.exe', 'ModularRuntime.Desktop.exe'
$missing = $expected | Where-Object { -not (Test-Path (Join-Path $Output $_)) }
if ($missing) {
    throw "Missing from the publish output: $($missing -join ', ')"
}

Write-Host ''
Write-Host "Published to $Output"
foreach ($exe in $expected) {
    $item = Get-Item (Join-Path $Output $exe)
    Write-Host ("  {0,-28} {1}" -f $item.Name, $item.VersionInfo.ProductVersion)
}
