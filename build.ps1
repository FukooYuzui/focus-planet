param([string]$Destination = (Join-Path $PSScriptRoot 'artifacts/FocusPlanet-Windows-x64'))
$ErrorActionPreference = 'Stop'
$Destination = [System.IO.Path]::GetFullPath($Destination)
dotnet publish (Join-Path $PSScriptRoot 'src/FocusGarden.App/FocusGarden.App.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $PSScriptRoot 'artifacts/build') -o $Destination
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Move-Item -LiteralPath (Join-Path $Destination 'FocusGarden.exe') -Destination (Join-Path $Destination '专注星球.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'browser-extension') -Destination $Destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $Destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $Destination -Force
Write-Host "已生成：$Destination"
