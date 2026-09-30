# Home UI/UX direction

Source: [How to turn your AI into a world-class designer](https://www.lennysnewsletter.com/p/how-to-turn-your-ai-into-a-world)

## What the article contributes

The article frames AI-assisted design as a three-part process:

1. Discover broadly before committing. Deliberately move away from the model's safest default and explore a stronger metaphor or point of view.
2. Define a distinct identity. Judge the whole composition, use concrete references, and iterate against an explicit quality bar instead of asking for vague improvement.
3. Deliver through restraint. Remove visual effects, labels, containers, and custom controls that do not help the user complete the task.

It also recommends using generated media when imagery genuinely carries the experience, separating implementation from independent critique, and checking the result in its real environment rather than trusting code alone.

## How that maps to this project

This project is a low-latency desktop companion, so generated imagery and decorative motion would work against the task. The chosen direction is a quiet native control surface based on the original Concept B baseline:

- An asymmetric light composition built around the supplied red-and-black app mark and a single red folded utility rail.
- A single dominant `Default` preset with automatic startup and no normal stop control. Closing the app releases the controller path and clears feedback.
- A compact live-state line and two quiet footer checks for DualSense USB and ACC telemetry.
- Progressive disclosure. Advanced settings and diagnostics remain visible entry points while the home screen stays immediately usable.
- Advanced settings are organized as five numbered modules: pedal resistance, braking cues, powertrain, traction, and surface/grip. Individual event rows expose an exact 0–100 value, a plain-language strength label and an output route. Redline onset is the only detection timing exposed; car-specific slip, ABS and shift detection remain calibrated automatically.
- Moving a slider activates `Custom` and saves automatically beside the portable executable after a short debounce. `0` is the only off state, `50` is the calibrated standard and `100` is a clearly stronger bounded output. Right-clicking any parameter row restores only that parameter to its calibrated default. Returning to `Default` bypasses every custom choice and restores the calibrated output bit-for-bit without discarding the saved Custom positions.
- Diagnostics has its own quiet page with the four signal-path states and a five-event, memory-only session log. A copy action creates a compact support summary without writing a log file during normal operation.
- Recovery guidance next to failure states, including USB, HidHide, ViGEmBus, and stale telemetry actions.
- No decorative animation, glow, fake gauges, WebView, or additional UI framework. Motion is limited to short page/state transitions, button press response and direct-manipulation feedback on slider thumbs.

## Design system

- Design read: precise and trustworthy, closer to a first-party system utility than a gaming overlay.
- Native stack: WPF on .NET 8, with no Chromium/WebView runtime and no permanent UI timer.
- Color system: `#F0F2F4` canvas, near-black text, the icon's `#E32232` brand red, plus semantic green and amber.
- Typography: Segoe UI Variable Display/Text with a compact native type scale and an 8-pixel spacing rhythm.
- `DESIGN_VARIANCE: 7`: a recognizable red fold and oversized preset typography without compromising utility.
- `MOTION_INTENSITY: 2`: 70–200 ms press, navigation and slider-thumb feedback only; page and state transitions honor the Windows client-area animation setting.
- `VISUAL_DENSITY: 3`: no live gauges or telemetry grid; driving detail appears as one quiet line only while feedback is live.
- Shape system: one open preset field, one compact segmented preset selector, flat numbered feedback sections with open-track sliders and switches, and one folded utility rail; cards are not used as the default layout primitive.
- Accessibility: 44-pixel primary targets, keyboard-operable controls, AutomationProperties on interactive controls, text labels in addition to color, and actionable status wording. Custom focus outlines are suppressed at the user's direction; selected and active states remain explicit in text, weight and position.

## Quality bar

The normal run mode should let a user answer these questions in under two seconds:

1. Is the DualSense controller path ready?
2. Is ACC connected?
3. Is feedback live or safely paused?
4. If something is wrong, what should I do next?

The interface should also remain visually calm when left open beside ACC. It is a companion, not the primary experience.
