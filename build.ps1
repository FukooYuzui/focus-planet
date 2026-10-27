param(
    [string]$Destination = (Join-Path $PSScriptRoot 'outputs/FocusPlanet-Windows-x64'),
    [string]$WorkDirectory = (Join-Path $PSScriptRoot 'work/build')
)
$ErrorActionPreference = 'Stop'
$Destination = [System.IO.Path]::GetFullPath($Destination)
$WorkDirectory = [System.IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $Destination -PathType Leaf) { throw '输出位置已被文件占用，请选择空的输出目录。' }
if ((Test-Path -LiteralPath $Destination -PathType Container) -and @(Get-ChildItem -LiteralPath $Destination -Force).Count -gt 0) {
    throw '输出目录不是空目录。请指定一个新的或空的目录，以免混入旧版本 DLL；脚本不会删除已有文件。'
}
$verificationArtifacts = Join-Path $WorkDirectory 'verification'
foreach ($testProject in @('FocusGarden.Core.Tests', 'FocusGarden.Upgrade.Tests', 'FocusGarden.PlanetLayout.Tests', 'FocusGarden.PlanetGrowth.Tests', 'FocusGarden.PlanetGeometry.Tests', 'FocusGarden.PlanetView.Tests', 'FocusGarden.PlanetIntegration.Tests', 'FocusGarden.Packaging.Tests', 'FocusGarden.StrictFocus.Tests', 'FocusGarden.Timing.Tests')) {
    dotnet build (Join-Path $PSScriptRoot "tests/$testProject/$testProject.csproj") -c Release --artifacts-path $verificationArtifacts --nologo
    if ($LASTEXITCODE -ne 0) { throw "数据保护检查构建失败，已停止发布：$testProject" }
    dotnet (Join-Path $verificationArtifacts "bin/$testProject/release/$testProject.dll")
    if ($LASTEXITCODE -ne 0) { throw "数据保护检查未通过，已停止发布：$testProject" }
}
dotnet publish (Join-Path $PSScriptRoot 'src/FocusGarden.App/FocusGarden.App.csproj') -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $WorkDirectory 'publish') -o $Destination -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw '构建失败。' }
if (!(Test-Path -LiteralPath (Join-Path $Destination 'FocusGarden.exe') -PathType Leaf)) { throw '单文件发布未生成主程序。' }
if (@(Get-ChildItem -LiteralPath $Destination -Force | Where-Object { $_.Name -ne 'FocusGarden.exe' }).Count -gt 0) {
    throw '单文件发布仍包含其他运行文件，已停止打包，请检查发布配置。'
}
Move-Item -LiteralPath (Join-Path $Destination 'FocusGarden.exe') -Destination (Join-Path $Destination '专注星球.exe') -Force
$extensionDestination = Join-Path $Destination 'browser-extension'
New-Item -ItemType Directory -Path $extensionDestination -Force | Out-Null
foreach ($extensionFile in @('background.js', 'controller.js', 'manifest.json', 'popup.css', 'popup.html', 'popup.js', 'rules.js', 'README.md')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "browser-extension/$extensionFile") -Destination $extensionDestination -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs') -Destination $Destination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $Destination -Force
Write-Host "已生成：$Destination"
