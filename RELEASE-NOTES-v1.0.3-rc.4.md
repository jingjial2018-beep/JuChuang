# JNest v1.0.3-rc.4 — Native modal input guard test

Based on `release/v1.0.3-rc3-modal`, replacing the temporary offscreen parking of native WeChat/WhatsApp HWNDs with a modal low-level mouse input guard.

- Keep hosted native WeChat/WhatsApp windows visible behind About / Settings dialogs.
- Block mouse clicks, double-clicks and wheel input targeted at hosted windows while a modal dialog is open.
- Retain modal centering, caption drag prevention, owner relationship and Z-order suppression.
- No global Topmost, no changes to account identity / notification functionality.

Windows x64 prerelease for manual testing: check dialog overlay above hosted clients; background input disabled; switching to another app can cover JNest; returning to JNest restores modal display.

FrameworkDependent build needs .NET 8 Desktop Runtime; SelfContained includes runtime. Unsigned test build.
