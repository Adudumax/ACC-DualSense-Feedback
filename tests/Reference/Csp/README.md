# CSP reference oracle

Unmodified source files from `ac-custom-shaders-patch/acc-lua-internal`, revision
`2031bd2d7c913e0aad3ff18a3bab86ffccad1326` (MIT, Copyright 2022 Ilja Jusupov).
See `licenses/CSP-LUA-INTERNAL-LICENSE.txt` at the repository root.

`generate-fixtures.lua` executes the original `ComputeHighlightColor()` and the
extracted, unmodified `updateDualSense()` function body under a small CSP-host
test double. Colour container/HSV operations, controller collections and Sim
are stubbed; the original colour branches, comparisons and LED code are executed.
The test double does not exercise CSP's native HID encoder or optical output.

`fixtures.json` contains 862 cases. C# tests require RGB error to remain within
half an 8-bit quantization step, with exact player masks and brightness values.

Regenerate from the repository root with `node tools/Generate-CspLightFixtures.cjs`
after installing the optional development-only Fengari runtime under ignored
`artifacts/csp-reference`. No Lua/Node runtime is shipped with the application.

ACC input adaptations and intentionally unconnected CSP inputs are documented in
`docs/CSP-LIGHTING-ANALYSIS.zh-CN.md`. Same-input oracle agreement is not a claim
that ACC supplies every AC/CSP signal or the same per-car shift range.
