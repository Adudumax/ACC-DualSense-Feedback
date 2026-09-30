param(
    [string]$OutputRoot = '',

    [ValidateRange(0, 400)]
    [int]$ExpectedDpiPercent = 0,

    [switch]$RequireMissingDrivers,
    [switch]$RunNuGetAudit
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repositoryRoot 'src\ACCDualSenseFeedback\ACCDualSenseFeedback.csproj'
$captureArchive = Join-Path $repositoryRoot 'tests\TelemetryCaptures.zip'
$legacyCaptureRoot = Join-Path $repositoryRoot 'dist\portable-win-x64-preview6-capture'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputRoot = Join-Path $repositoryRoot "artifacts\release-preflight-$stamp"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot = Join-Path $repositoryRoot $OutputRoot
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) {
    throw "Preflight output already exists: $OutputRoot"
}

$publishPath = Join-Path $OutputRoot 'portable-win-x64'
$verificationPath = Join-Path $OutputRoot 'verification'
New-Item -ItemType Directory -Path $publishPath, $verificationPath -Force | Out-Null

function Invoke-Candidate {
    param(
        [Parameter(Mandatory)] [string]$Description,
        [Parameter(Mandatory)] [string[]]$Arguments
    )

    $process = Start-Process `
        -FilePath $script:executablePath `
        -ArgumentList $Arguments `
        -Wait `
        -PassThru `
        -NoNewWindow
    if ($process.ExitCode -ne 0) {
        throw "$Description failed with exit code $($process.ExitCode)."
    }
}

Write-Host 'Restoring release dependencies...'
$restoreArguments = @('restore', $projectPath, '--runtime', 'win-x64', '--force-evaluate')
if ($RunNuGetAudit) {
    $restoreArguments += @(
        '-p:NuGetAudit=true',
        '-p:NuGetAuditMode=all',
        '-warnaserror:NU1901;NU1902;NU1903;NU1904'
    )
}
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

Write-Host 'Publishing isolated release candidate...'
& dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishPath
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$executablePath = Join-Path $publishPath 'ACCDualSenseFeedback.exe'
if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw 'Published executable is missing.'
}

Write-Host 'Running deterministic self-test...'
Invoke-Candidate -Description 'Packaged executable self-test' -Arguments @('--self-test')

$environmentReportPath = Join-Path $verificationPath 'environment-report.txt'
Invoke-Candidate -Description 'Environment report' -Arguments @(
    '--environment-report',
    ('"' + $environmentReportPath + '"')
)
$environmentReport = Get-Content -Raw -LiteralPath $environmentReportPath
foreach ($requiredMarker in @(
    'ACC DualSense Feedback diagnostic report',
    '[Driver checks]',
    'ViGEmBus service registration:',
    'HidHide service registration:',
    '[DualSense HID probe]'
)) {
    if ($environmentReport.IndexOf($requiredMarker, [StringComparison]::Ordinal) -lt 0) {
        throw "Environment report is missing: $requiredMarker"
    }
}
if ($environmentReport -match '\\\\[?]\\hid#') {
    throw 'Environment report exposed a private HID device path.'
}
if ($RequireMissingDrivers) {
    foreach ($requiredMissingState in @(
        'ViGEmBus service registration: missing',
        'HidHide service registration: missing',
        'Visible and openable gamepad interfaces: 0'
    )) {
        if ($environmentReport.IndexOf($requiredMissingState, [StringComparison]::Ordinal) -lt 0) {
            throw "Clean-machine expectation failed: $requiredMissingState"
        }
    }
}

$snapshots = @(
    @{ Name = 'home'; Arguments = @() },
    @{ Name = 'home-controller-error'; Arguments = @('--controller-error') },
    @{ Name = 'settings'; Arguments = @('--settings') },
    @{ Name = 'diagnostics'; Arguments = @('--diagnostics') }
)
$snapshotResults = [System.Collections.Generic.List[string]]::new()
Add-Type -AssemblyName System.Drawing
foreach ($snapshot in $snapshots) {
    $snapshotPath = Join-Path $verificationPath "$($snapshot.Name).png"
    $arguments = @('--snapshot-ui', ('"' + $snapshotPath + '"')) + $snapshot.Arguments
    Invoke-Candidate -Description "$($snapshot.Name) UI snapshot" -Arguments $arguments
    if (-not (Test-Path -LiteralPath $snapshotPath -PathType Leaf)) {
        throw "UI snapshot is missing: $snapshotPath"
    }

    $image = [System.Drawing.Image]::FromFile($snapshotPath)
    try {
        $width = $image.Width
        $height = $image.Height
    }
    finally {
        $image.Dispose()
    }

    $dpiFromWidth = [int][Math]::Round($width * 100.0 / 920.0)
    $dpiFromHeight = [int][Math]::Round($height * 100.0 / 580.0)
    if ($dpiFromWidth -ne $dpiFromHeight) {
        throw "Inconsistent UI scaling in $($snapshot.Name): ${width}x${height}."
    }
    if ($ExpectedDpiPercent -ne 0 -and $dpiFromWidth -ne $ExpectedDpiPercent) {
        throw "$($snapshot.Name) rendered at $dpiFromWidth%, expected $ExpectedDpiPercent%."
    }
    $snapshotResults.Add("$($snapshot.Name): ${width}x${height}, ${dpiFromWidth}% DPI")
}

Write-Host 'Replaying accepted telemetry captures...'
$captureRoot = Join-Path $OutputRoot 'telemetry-captures'
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
if ($captureFiles.Count -eq 0) {
    throw "No accepted telemetry captures found in $captureRoot."
}
foreach ($captureFile in $captureFiles) {
    $replayPath = Join-Path $verificationPath "$($captureFile.BaseName)-replay.csv"
    Invoke-Candidate -Description "Telemetry replay $($captureFile.Name)" -Arguments @(
        '--replay-telemetry',
        ('"' + $captureFile.FullName + '"'),
        '--replay-output',
        ('"' + $replayPath + '"')
    )
    if (-not (Test-Path -LiteralPath $replayPath -PathType Leaf)) {
        throw "Telemetry replay output is missing: $($captureFile.Name)"
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
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $relativePath) -PathType Leaf)) {
        throw "Required release file is missing: $relativePath"
    }
}
if (Get-ChildItem -LiteralPath $publishPath -Recurse -File -Filter 'ACCDualSenseFeedback.settings.json') {
    throw 'Release candidate contains a personal settings file.'
}

$fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath)
if ($fileVersion.FileVersion -ne '1.0.0.0' -or $fileVersion.ProductVersion -ne '1.0.0') {
    throw "Unexpected executable version: file=$($fileVersion.FileVersion), product=$($fileVersion.ProductVersion)"
}

$signature = Get-AuthenticodeSignature -LiteralPath $executablePath
$summaryPath = Join-Path $verificationPath 'preflight-summary.txt'
$summary = @(
    'ACC DualSense Feedback release preflight passed',
    "Executable: $executablePath",
    "Version: $($fileVersion.ProductVersion)",
    "Signature: $($signature.Status)",
    "Telemetry captures replayed: $($captureFiles.Count)"
)
$summary += $snapshotResults.ToArray()
$summary += "Environment report: $environmentReportPath"
[System.IO.File]::WriteAllLines($summaryPath, $summary, [System.Text.UTF8Encoding]::new($false))

Write-Host "Release preflight passed: $OutputRoot"
$summary | ForEach-Object { Write-Host $_ }
