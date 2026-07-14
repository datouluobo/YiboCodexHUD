param(
    [string]$Version = "1.0.5",
    [string]$PublishDir = ".tmp\release\v$Version\win-x64",
    [string]$OutputDir = ".tmp\installer\v$Version",
    [string]$DistributionOutputDir = "dist\installer"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishPath = Join-Path $repoRoot $PublishDir
$outputPath = Join-Path $repoRoot $OutputDir
$payloadDir = Join-Path $outputPath "payload"
$payloadZipPath = Join-Path $outputPath "payload.zip"
$publishOutputDir = Join-Path $outputPath "publish"
$setupProjectPath = Join-Path $PSScriptRoot "YiboCodexHUD.Setup\YiboCodexHUD.Setup.csproj"
$targetName = Join-Path $outputPath "YiboCodexHUD-Setup-v$Version.exe"
$distributionOutputPath = Join-Path $repoRoot $DistributionOutputDir
$distributionVersionedTargetName = Join-Path $distributionOutputPath "YiboCodexHUD-Setup-v$Version.exe"

if (-not (Test-Path $publishPath)) {
    throw "Publish output not found: $publishPath"
}

if (-not (Test-Path $setupProjectPath)) {
    throw "Setup project not found: $setupProjectPath"
}

New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
New-Item -ItemType Directory -Force -Path $publishOutputDir | Out-Null
New-Item -ItemType Directory -Force -Path $distributionOutputPath | Out-Null

Get-ChildItem -LiteralPath $payloadDir -Force -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse
Get-ChildItem -LiteralPath $publishOutputDir -Force -ErrorAction SilentlyContinue | Remove-Item -Force -Recurse

Get-ChildItem -LiteralPath $publishPath -File |
    Where-Object { $_.Extension -notin @(".pdb") } |
    Copy-Item -Destination $payloadDir -Force

if (Test-Path $payloadZipPath) {
    Remove-Item $payloadZipPath -Force
}

Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $payloadZipPath -CompressionLevel Optimal -Force

$payloadZipFullPath = (Resolve-Path $payloadZipPath).Path

dotnet publish $setupProjectPath `
    -c Release `
    -r win-x64 `
    -p:Version=$Version `
    -p:PayloadZipPath="$payloadZipFullPath" `
    -p:SelfContained=true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugSymbols=false `
    -p:DebugType=None `
    -o $publishOutputDir

$publishedExe = Join-Path $publishOutputDir "YiboCodexHUD.Setup.exe"
if (-not (Test-Path $publishedExe)) {
    throw "Setup executable was not created: $publishedExe"
}

Copy-Item -LiteralPath $publishedExe -Destination $targetName -Force
Copy-Item -LiteralPath $publishedExe -Destination $distributionVersionedTargetName -Force
Get-Item $targetName | Select-Object FullName, Length, LastWriteTime
