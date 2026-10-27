param([string]$Compiler)
$ErrorActionPreference = 'Stop'
$nativeRoot = $PSScriptRoot
$projectRoot = Split-Path $nativeRoot -Parent
if (!$Compiler) {
    $portable = Join-Path $projectRoot 'artifacts/tools/llvm-mingw/llvm-mingw-20261020-ucrt-x86_64/bin/x86_64-w64-mingw32-clang++.exe'
    if (Test-Path $portable) { $Compiler = $portable }
    elseif (Get-Command clang++ -ErrorAction SilentlyContinue) { $Compiler = (Get-Command clang++).Source }
    elseif (Get-Command cl -ErrorAction SilentlyContinue) { $Compiler = (Get-Command cl).Source }
    else { throw 'Install or supply a C++17 x64 compiler using -Compiler. See native/README.md.' }
}
$outputRoot = Join-Path $nativeRoot 'bin/win-x64'
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$output = Join-Path $outputRoot 'ACCGyroFusion.dll'
$source = Join-Path $nativeRoot 'gyro_fusion.cpp'
if ((Split-Path $Compiler -Leaf) -eq 'cl.exe') {
    & $Compiler /nologo /std:c++17 /O2 /EHsc /MT /LD $source "/Fo$outputRoot/gyro_fusion.obj" /link "/OUT:$output" "/IMPLIB:$outputRoot/ACCGyroFusion.lib"
} else {
    & $Compiler --target=x86_64-w64-mingw32 -std=c++17 -O2 -shared -static -static-libgcc -static-libstdc++ -fno-exceptions -fno-rtti $source -o $output
}
if ($LASTEXITCODE -ne 0) { throw "Native fusion build failed: $LASTEXITCODE" }
Get-Item $output | Select-Object FullName,Length
Get-FileHash $output -Algorithm SHA256
