# Third-party notices

The project's own code is licensed under MIT; see the repository's `LICENSE`
or `licenses/ACC-DUALSENSE-FEEDBACK-LICENSE.txt` in portable distributions.
The following third-party components and fonts retain their respective terms.

## ACC shared memory references

The ACC shared-memory field order follows Kunos' ACC Shared Memory documentation. The MIT-licensed PyAccSharedMemory project was used as a cross-check:

https://github.com/rrennoir/PyAccSharedMemory

## CSP controller lighting

The controller-light colour function and player-LED rules are ported from
`led_colors.lua` and `dualsense_lights.lua` in Ilja Jusupov's CSP Lua internals,
revision `2031bd2d7c913e0aad3ff18a3bab86ffccad1326`.

Source: https://github.com/ac-custom-shaders-patch/acc-lua-internal<br>
License: MIT, Copyright (c) 2022 Ilja Jusupov; reproduced in
`licenses/CSP-LUA-INTERNAL-LICENSE.txt`. Original reference scripts and
development-only comparison fixtures are kept in `tests/Reference/Csp` and are
not bundled into the portable application. ACC input limitations are described
in `docs/CSP-LIGHTING-ANALYSIS.zh-CN.md` in the source repository.

## Nefarius ViGEm .NET Client

The integrated virtual Xbox 360 bridge uses `Nefarius.ViGEm.Client` 1.21.256.

Source: https://github.com/nefarius/ViGEm.NET<br>
NuGet: https://www.nuget.org/packages/Nefarius.ViGEm.Client/1.21.256<br>
License: MIT; reproduced in `licenses/VIGEM-NET-LICENSE.txt`.

## Gyro sensor fusion

`ACCGyroFusion.dll` contains GamepadMotionHelpers version 10, fixed at commit
`f1a76007ec058122a4bb6f19c816c6c0c7b7f12a`, with this project's C ABI wrapper.
The upstream header is kept unmodified in `native/vendor/GamepadMotionHelpers`.

Source: https://github.com/JibbSmart/GamepadMotionHelpers<br>
License: MIT, Copyright (c) 2020-2023 Julian "Jibb" Smart; reproduced in
`licenses/GAMEPAD-MOTION-HELPERS-LICENSE.txt`.

The Windows x64 helper is built with LLVM MinGW; its statically linked LLVM
support code retains the Apache 2.0 with LLVM Exceptions terms reproduced in
`licenses/LLVM-RUNTIME-LICENSE.txt`. The development compiler is not distributed.
The DLL uses only Windows system DLLs at runtime.

DualSense IMU report parsing and factory calibration are independently implemented
from the protocol layout and conversion formulas documented in SDL's PS5 driver:
https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c

## Chinese UI font (zh-CN builds only)

The Simplified Chinese build embeds subsets of Noto Sans SC, instantiated at
weights 400, 500 and 600 and renamed **ACC Noto UI SC**. English builds do not
embed this font. The fonts are not installed into the operating system.

Source: https://github.com/google/fonts/tree/main/ofl/notosanssc<br>
License: SIL Open Font License 1.1; reproduced in `licenses/NOTO-SANS-SC-OFL.txt`
in Chinese portable packages. Copyright notices are retained in the font files.

## Technical-reference acknowledgement

Thanks to Hamza Yeşilmen (HamzaYslmn) and the Forza Horizon DualSense Python project for technical reference on DualSense adaptive-trigger behavior:

https://github.com/HamzaYslmn/Forza-Horizon-DualSense-Python

This project is acknowledged as a reference only and is not included in this distribution.
