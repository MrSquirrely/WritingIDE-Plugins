<#
.SYNOPSIS
    Builds the plugins and zips each one for a release.

.DESCRIPTION
    Builds every plugin in Release and writes artifacts\<Plugin>-<version>.zip, with the plugin's own files (not
    WritingIDE's SDK and Core, which WritingIDE supplies). It prints each zip's SHA-256: a plugin's listing in
    WritingIDE's discussions needs the download link and that checksum.

.PARAMETER Version
    The version in the zip names. Default: the <Version> in Directory.Build.props.

.EXAMPLE
    .\build\package.ps1
    gh release create v1.0.0 (Get-ChildItem artifacts\*.zip) --title "1.0.0"
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$WritingIdeRoot
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
}
$artifacts = Join-Path $repo 'artifacts'
New-Item $artifacts -ItemType Directory -Force | Out-Null

foreach ($project in Get-ChildItem (Join-Path $repo 'src') -Directory) {
    $arguments = @('build', $project.FullName, '-c', 'Release', '-nologo', '-v', 'q')
    if ($WritingIdeRoot) {
        $arguments += "-p:WritingIdeRoot=$WritingIdeRoot"
    }
    & dotnet @arguments | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Building $($project.Name) failed."
    }
    $output = Join-Path $project.FullName 'bin\Release\net10.0-windows'
    $files = Get-ChildItem $output -File | Where-Object { ($_.Extension -eq '.dll') -and ($_.Name -notlike 'WritingIDE.*') }
    $zip = Join-Path $artifacts "$($project.Name)-$Version.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path $files.FullName -DestinationPath $zip
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "$($project.Name)-$Version.zip  sha256: $hash"
}