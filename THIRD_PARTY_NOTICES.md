# Third-party notices

The project's own code is licensed under MIT; see the repository's `LICENSE`
or `licenses/ACC-DUALSENSE-FEEDBACK-LICENSE.txt` in portable distributions.
The following third-party components and fonts retain their respective terms.

## ACC shared memory references

The ACC shared-memory field order follows Kunos' ACC Shared Memory documentation. The MIT-licensed PyAccSharedMemory project was used as a cross-check:

https://github.com/rrennoir/PyAccSharedMemory

## Nefarius ViGEm .NET Client

The integrated virtual Xbox 360 bridge uses `Nefarius.ViGEm.Client` 1.21.256.

Source: https://github.com/nefarius/ViGEm.NET<br>
NuGet: https://www.nuget.org/packages/Nefarius.ViGEm.Client/1.21.256<br>
License: MIT; reproduced in `licenses/VIGEM-NET-LICENSE.txt`.

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
