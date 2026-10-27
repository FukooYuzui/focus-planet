param([string]$Destination = (Join-Path $PSScriptRoot 'artifacts/FocusPlanet-Windows-x64'))
$ErrorActionPreference = 'Stop'
$Destination = [System.IO.Path]::GetFullPath($Destination)
$verificationArtifacts = Join-Path $PSScriptRoot 'artifacts/verification'
foreach ($testProject in @('FocusGarden.Core.Tests', 'FocusGarden.Upgrade.Tests', 'FocusGarden.PlanetIntegration.Tests')) {
    dotnet build (Join-Path $PSScriptRoot "tests/$testProject/$testProject.csproj") -c Release --artifacts-path $verificationArtifacts --nologo
    if ($LASTEXITCODE -ne 0) { throw "数据保护检查构建失败，已停止发布：$testProject" }
    dotnet (Join-Path $verificationArtifacts "bin/$testProject/release/$testProject.dll")
    if ($LASTEXITCODE -ne 0) { throw "数据保护检查未通过，已停止发布：$testProject" }
}
dotnet publish (Join-Path $PSScriptRoot 'src/FocusGarden.App/FocusGarden.App.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $PSScriptRoot 'artifacts/build') -o $Destination
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
Move-Item -LiteralPath (Join-Path $Destination 'FocusGarden.exe') -Destination (Join-Path $Destination '专注星球.exe') -Force
$extensionDestination = Join-Path $Destination 'browser-extension'
New-Item -ItemType Directory -Path $extensionDestination -Force | Out-Null
foreach ($extensionFile in @('background.js', 'controller.js', 'manifest.json', 'popup.css', 'popup.html', 'popup.js', 'rules.js', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "browser-extension/$extensionFile") -Destination $extensionDestination -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $Destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $Destination -Force
Write-Host "已生成：$Destination"
