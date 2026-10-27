# Gyro fusion dependency

`vendor/GamepadMotionHelpers/GamepadMotion.hpp` is the unmodified MIT-licensed upstream header from
https://github.com/JibbSmart/GamepadMotionHelpers, commit `f1a76007ec058122a4bb6f19c816c6c0c7b7f12a` (version 10).
The adjacent `LICENSE` is copied from the same commit. `gyro_fusion.cpp` exposes a versioned C ABI only;
device access, steering, neutral pose and explicit stationary bias calibration remain in C#.
GMH automatic bias learning is deliberately disabled at all times. The wrapper uses GMH's accelerometer
shakiness weighting and reduces correction further when acceleration magnitude deviates from 1 g.

Build from the project root:

```powershell
./native/build.ps1 -Compiler <path-to-x64-clang++.exe-or-cl.exe>
```

Output: `native/bin/win-x64/ACCGyroFusion.dll`. The portable application copies this next to its EXE.
No controller is opened by this build or the deterministic gyro tests.

The development build uses official portable llvm-mingw 20261020 UCRT x86_64 from
https://github.com/mstorsjo/llvm-mingw/releases/tag/20261020 (no system installation).
Its release archive SHA-256, verified against GitHub's asset digest before extraction:
`fd5462b7f5d93b2ff981fcd3f9c7e427e47f3203c64ea91c27bcc444eebd74cd`.
It can be extracted under `artifacts/tools/llvm-mingw/`; the build script detects that location.
The DLL links the C++ support runtime statically. Only Windows system runtime DLLs are required.
