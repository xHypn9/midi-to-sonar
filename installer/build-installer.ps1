[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [string]$ProductVersion = "1.3.5"
)

$ErrorActionPreference = "Stop"

$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $PSScriptRoot "output"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$buildRoot = [System.IO.Path]::GetFullPath((Join-Path $tempRoot "Midi2SonarInstallerBuild"))
if (-not $buildRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe installer build directory: $buildRoot"
}
$wixToolDirectory = Join-Path $buildRoot "wix-5.0.0"
$publishDirectory = Join-Path $buildRoot "publish"
$artifactsDirectory = Join-Path $buildRoot "artifacts"
$wixExecutable = Join-Path $wixToolDirectory "wix.exe"
$projectPath = Join-Path $sourceRoot "SonarMidiController.csproj"
$productDefinition = Join-Path $PSScriptRoot "Product.wxs"
$iconPath = Join-Path $sourceRoot "Assets\MidiToSonar.ico"
$publishedExecutable = Join-Path $publishDirectory "SteelSeriesSonarMidiController.exe"
$portablePath = Join-Path $OutputDirectory "MIDI to Sonar.exe"
$installerPath = Join-Path $OutputDirectory "MIDI-to-Sonar-Setup-$ProductVersion.msi"

if (Test-Path -LiteralPath $buildRoot) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if (-not (Test-Path -LiteralPath $wixExecutable)) {
    dotnet tool install wix --version 5.0.0 --tool-path $wixToolDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to install the local WiX build tool."
    }
}

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PublishTrimmed=false `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -p:Deterministic=true `
    -p:ContinuousIntegrationBuild=true `
    --artifacts-path $artifactsDirectory `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Application publishing failed."
}

Copy-Item -LiteralPath $publishedExecutable -Destination $portablePath -Force

& $wixExecutable build $productDefinition `
    -arch x64 `
    -d "ProductVersion=$ProductVersion" `
    -d "SourceExe=$publishedExecutable" `
    -d "IconFile=$iconPath" `
    -intermediateFolder (Join-Path $buildRoot "wix-obj") `
    -pdbtype none `
    -out $installerPath
if ($LASTEXITCODE -ne 0) {
    throw "Installer creation failed."
}

Write-Output $portablePath
Write-Output $installerPath
