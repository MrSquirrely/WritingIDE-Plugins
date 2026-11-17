<#
.SYNOPSIS
    Builds the plugins and installs them into WritingIDE.

.DESCRIPTION
    Builds every plugin in Release, then copies each one into its own folder under
    %LocalAppData%\WritingIDE\Extensions. Restart WritingIDE afterwards. Close WritingIDE first when
    updating plugins that are already installed, because it keeps their files open.

.PARAMETER Plugin
    Install only these plugins (folder names under src). Default: all of them.

.PARAMETER WritingIdeRoot
    The WritingIDE source folder to build against. Default: ..\WritingIDE next to this repository.

.PARAMETER Uninstall
    Remove the plugins instead of installing them.

.EXAMPLE
    .\build\install.ps1
    .\build\install.ps1 -Plugin MapMaker, Gamification
    .\build\install.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string[]]$Plugin,
    [string]$WritingIdeRoot,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$target = Join-Path $env:LOCALAPPDATA 'WritingIDE\Extensions'
$projects = Get-ChildItem (Join-Path $repo 'src') -Directory | Where-Object { -not $Plugin -or ($Plugin -contains $_.Name) }
if (-not $projects) {
    throw "No plugin matches: $($Plugin -join ', ')"
}

foreach ($project in $projects) {
    $destination = Join-Path $target $project.Name
    if ($Uninstall) {
        if (Test-Path $destination) {
            Remove-Item $destination -Recurse -Force
            Write-Host "Removed $($project.Name)"
        }
        continue
    }

    $arguments = @('build', $project.FullName, '-c', 'Release', '-nologo', '-v', 'q')
    if ($WritingIdeRoot) {
        $arguments += "-p:WritingIdeRoot=$WritingIdeRoot"
    }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Building $($project.Name) failed."
    }

    $output = Join-Path $project.FullName 'bin\Release\net10.0-windows'
    New-Item $destination -ItemType Directory -Force | Out-Null
    # The plugin and whatever it brings, but not WritingIDE's own assemblies: WritingIDE supplies those.
    Get-ChildItem $output -File |
        Where-Object { ($_.Extension -in '.dll', '.pdb') -and ($_.Name -notlike 'WritingIDE.*') } |
        Copy-Item -Destination $destination -Force
    Write-Host "Installed $($project.Name) to $destination"
}

if (-not $Uninstall) {
    Write-Host 'Restart WritingIDE to load the plugins (Tools > Extensions and API lists them).'
}