# Fails when the vpk tool and the Velopack package have different versions, so a
# Velopack update can't merge half done.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tool = (Get-Content "$root/.config/dotnet-tools.json" -Raw | ConvertFrom-Json).tools.vpk.version
$package = ([xml](Get-Content "$root/Directory.Packages.props" -Raw)).Project.ItemGroup.PackageVersion |
    Where-Object Include -eq 'Velopack' | Select-Object -ExpandProperty Version
if (-not $tool -or -not $package) { throw "Couldn't read the vpk tool ($tool) or Velopack package ($package) version." }
if ($tool -ne $package) { throw "vpk tool is $tool but the Velopack package is $package. Update both together." }
Write-Host "vpk and Velopack are both $tool."
