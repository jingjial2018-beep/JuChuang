# JNest v1.0.6-beta.7 — Modal shield mouse hit testing

**Unsigned Windows beta.** Earlier betas remain available.

## Changes
- Replace the shield's native `STATIC` window class with a mouse-hit-testable `BUTTON` class. Beta.6 logs showed the shield was visible, enabled and above the client, yet every click still hit the client. Windows deliberately skips static text controls during `WindowFromPoint` hit testing.
- Continue consuming the shield click and restoring the deepest visible JNest modal without sending the click to the native chat.
- Include shield and client screen rectangles in the click diagnostics to distinguish a hit-test issue from a bounds or DPI issue during hands-on validation.

## Validation
Windows CI compiles the WPF Release build and runs ArchitectureProbe and BadgeProbe. A local native-window probe confirmed the `BUTTON` class is hit at 1/255 layered opacity while `STATIC` is skipped. Actual first-click return, nested dialogs and mixed DPI still require Windows hands-on acceptance.
