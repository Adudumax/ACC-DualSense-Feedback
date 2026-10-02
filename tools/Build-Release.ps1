param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.1',

    [string]$OutputRoot = '',

    [ValidateSet('en', 'zh-CN')]
    [string]$Language = 'en'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'src\ACCDualSenseFeedback\ACCDualSenseFeedback.csproj'
$languageSuffix = if ($Language -eq 'zh-CN') { '-zh-CN' } else { '' }
$releaseRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repositoryRoot "dist\release-v$Version$languageSuffix"
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputRoot))
}
$portableName = "ACCDualSenseFeedback-v$Version-win-x64$languageSuffix-portable"
$portablePath = Join-Path $releaseRoot $portableName
$zipPath = Join-Path $releaseRoot "$portableName.zip"
$assemblyVersion = "$Version.0"

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
    -p:Version=$Version `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:InformationalVersion=$Version `
    -p:UiLanguage=$Language `
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
    'licenses\ACC-DUALSENSE-FEEDBACK-LICENSE.txt',
    'licenses\THIRD_PARTY_NOTICES.md',
    'licenses\VIGEM-NET-LICENSE.txt'
)
$requiredRelativePaths += if ($Language -eq 'zh-CN') {
    $chineseReadmeName = (-join @(
        [char]0x4F7F, [char]0x7528, [char]0x8BF4, [char]0x660E
    )) + '.txt'
    $chineseReadmeName, 'licenses\NOTO-SANS-SC-OFL.txt'
} else {
    'README.txt'
}
foreach ($relativePath in $requiredRelativePaths) {
    $requiredPath = Join-Path $portablePath $relativePath
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Required release file is missing: $relativePath"
    }
}

$portablePrefix = $portablePath.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$actualRelativePaths = @(
    Get-ChildItem -LiteralPath $portablePath -Recurse -File |
        ForEach-Object {
            if (-not $_.FullName.StartsWith($portablePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
                throw "Published file escaped the portable directory: $($_.FullName)"
            }
            $_.FullName.Substring($portablePrefix.Length).Replace('/', '\')
        } |
        Sort-Object
)
$allowedRelativePaths = @($requiredRelativePaths | Sort-Object)
$unexpectedPaths = @($actualRelativePaths | Where-Object { $_ -notin $allowedRelativePaths })
$missingPaths = @($allowedRelativePaths | Where-Object { $_ -notin $actualRelativePaths })
if ($unexpectedPaths.Count -ne 0 -or $missingPaths.Count -ne 0) {
    throw "Release content mismatch. Unexpected=[$($unexpectedPaths -join ', ')] Missing=[$($missingPaths -join ', ')]"
}

$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath)
if ($fileVersion.FileVersion -ne $assemblyVersion -or $fileVersion.ProductVersion -ne $Version) {
    throw "Unexpected executable version: file=$($fileVersion.FileVersion), product=$($fileVersion.ProductVersion)"
}

$compressionLevel = if ([Enum]::GetNames([System.IO.Compression.CompressionLevel]) -contains 'SmallestSize') {
    [Enum]::Parse([System.IO.Compression.CompressionLevel], 'SmallestSize')
}
else {
    [System.IO.Compression.CompressionLevel]::Optimal
}

[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $portablePath,
    $zipPath,
    $compressionLevel,
    $true)

$releaseManifestPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$releaseManifest = @(
    "$((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($zipPath))",
    "$((Get-FileHash -Algorithm SHA256 -LiteralPath $executablePath).Hash.ToLowerInvariant())  $portableName/ACCDualSenseFeedback.exe"
)
[System.IO.File]::WriteAllLines($releaseManifestPath, $releaseManifest, [System.Text.UTF8Encoding]::new($false))

Write-Host "Release prepared: $releaseRoot"
Write-Host "Portable ZIP: $zipPath"
Write-Host "Executable version: $($fileVersion.ProductVersion)"
Write-Host "UI language: $Language"
