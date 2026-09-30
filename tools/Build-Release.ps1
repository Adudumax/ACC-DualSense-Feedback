param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0',

    [string]$OutputRoot = ''
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'src\ACCDualSenseFeedback\ACCDualSenseFeedback.csproj'
$releaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repositoryRoot "dist\release-v$Version"
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
$portableName = "ACCDualSenseFeedback-v$Version-win-x64-portable"
$portablePath = Join-Path $releaseRoot $portableName
$zipPath = Join-Path $releaseRoot "$portableName.zip"

if (Test-Path -LiteralPath $releaseRoot) {
    throw "Release output already exists: $releaseRoot. Preserve it or move it before rebuilding."
}

New-Item -ItemType Directory -Path $portablePath -Force | Out-Null

& dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $portablePath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $portablePath 'ACCDualSenseFeedback.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw 'Published executable is missing.'
}

$requiredRelativePaths = @(
    'ACCDualSenseFeedback.exe',
    'README.txt',
    'licenses\THIRD_PARTY_NOTICES.md',
    'licenses\FORZA-DUALSENSE-LICENSE.txt',
    'licenses\VIGEM-NET-LICENSE.txt'
)
foreach ($relativePath in $requiredRelativePaths) {
    $requiredPath = Join-Path $portablePath $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release file is missing: $relativePath"
    }
}

$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath)
if ($fileVersion.FileVersion -ne '1.0.0.0' -or $fileVersion.ProductVersion -ne $Version) {
    throw "Unexpected executable version: file=$($fileVersion.FileVersion), product=$($fileVersion.ProductVersion)"
}

Compress-Archive -LiteralPath $portablePath -DestinationPath $zipPath -CompressionLevel Optimal

$releaseManifestPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$releaseManifest = @(
    "$((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($zipPath))",
    "$((Get-FileHash -Algorithm SHA256 -LiteralPath $executablePath).Hash.ToLowerInvariant())  $portableName/ACCDualSenseFeedback.exe"
)
[System.IO.File]::WriteAllLines($releaseManifestPath, $releaseManifest, [System.Text.UTF8Encoding]::new($false))

Write-Host "Release prepared: $releaseRoot"
Write-Host "Portable ZIP: $zipPath"
Write-Host "Executable version: $($fileVersion.ProductVersion)"
