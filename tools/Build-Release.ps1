param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '1.0.0'
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'src\ACCDualSenseFeedback\ACCDualSenseFeedback.csproj'
$releaseRoot = Join-Path $repositoryRoot "dist\release-v$Version"
$portableName = "ACCDualSenseFeedback-v$Version-win-x64-portable"
$portablePath = Join-Path $releaseRoot $portableName
$zipPath = Join-Path $releaseRoot "$portableName.zip"
$captureArchive = Join-Path $repositoryRoot 'tests\TelemetryCaptures.zip'
$legacyCaptureRoot = Join-Path $repositoryRoot 'dist\portable-win-x64-preview6-capture'

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
$selfTest = Start-Process -FilePath $executablePath -ArgumentList '--self-test' -Wait -PassThru
if ($selfTest.ExitCode -ne 0) {
    throw "Packaged executable self-test failed with exit code $($selfTest.ExitCode)."
}

$verificationRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ACCDualSenseFeedback-release-$Version-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $verificationRoot -Force | Out-Null
try {
    $captureRoot = Join-Path $verificationRoot 'telemetry-captures'
    if (Test-Path -LiteralPath $captureArchive -PathType Leaf) {
        New-Item -ItemType Directory -Path $captureRoot -Force | Out-Null
        Expand-Archive -LiteralPath $captureArchive -DestinationPath $captureRoot
    }
    elseif (Test-Path -LiteralPath $legacyCaptureRoot -PathType Container) {
        $captureRoot = $legacyCaptureRoot
    }
    else {
        throw "Accepted telemetry capture archive is missing: $captureArchive"
    }

    $captureFiles = Get-ChildItem -LiteralPath $captureRoot -Filter '*.csv' -File
    foreach ($captureFile in $captureFiles) {
        $replayPath = Join-Path $verificationRoot "$($captureFile.BaseName)-replay.csv"
        $replay = Start-Process -FilePath $executablePath -ArgumentList @(
            '--replay-telemetry',
            ('"' + $captureFile.FullName + '"'),
            '--replay-output',
            ('"' + $replayPath + '"')
        ) -Wait -PassThru
        if ($replay.ExitCode -ne 0) {
            throw "Telemetry replay failed: $($captureFile.Name)"
        }
    }

    $snapshotPath = Join-Path $verificationRoot 'diagnostics-150pct.png'
    $snapshot = Start-Process -FilePath $executablePath -ArgumentList @(
        '--snapshot-ui',
        ('"' + $snapshotPath + '"'),
        '--diagnostics'
    ) -Wait -PassThru
    if ($snapshot.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $snapshotPath)) {
        throw 'Diagnostics UI snapshot failed.'
    }

    Copy-Item -LiteralPath $snapshotPath -Destination (Join-Path $releaseRoot 'diagnostics-150pct.png')
}
finally {
    if (Test-Path -LiteralPath $verificationRoot) {
        Remove-Item -LiteralPath $verificationRoot -Recurse -Force
    }
}

$requiredRelativePaths = @(
    'ACCDualSenseFeedback.exe',
    'Capture Telemetry.cmd',
    'README.md',
    'RELEASE_NOTES.md',
    'THIRD_PARTY_NOTICES.md',
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

$internalManifestPath = Join-Path $portablePath 'SHA256SUMS.txt'
$manifestLines = Get-ChildItem -LiteralPath $portablePath -Recurse -File |
    Where-Object { $_.FullName -ne $internalManifestPath } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($portablePath.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
[System.IO.File]::WriteAllLines($internalManifestPath, $manifestLines, [System.Text.UTF8Encoding]::new($false))

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
