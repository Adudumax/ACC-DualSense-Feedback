# Telemetry replay fixtures

`TelemetryCaptures.zip` contains the nine accepted ACC telemetry sessions used to
verify the release feedback engine. The archive is lossless; the CSV files are
the same captures used to calibrate and approve the initial `v1.0.0` release.

The fixtures contain numerical vehicle telemetry, native motor values and
calculated feedback output. They do not contain a Windows user name, filesystem
path, controller device path or other account identifier.

Both `tools/Test-ReleaseCandidate.ps1` and `tools/Build-Release.ps1` extract the
archive into temporary validation output before replaying every capture.
