# JNest v1.0.6-beta.6 — Native modal input shield

**Unsigned Windows beta.** v1.0.6-beta.4 and beta.5 remain available.

## Changes
- Replace the beta.5 WPF `HwndSource` click shield with an owned native popup. On the reported machine, WPF discarded `WS_EX_LAYERED`; `SetLayeredWindowAttributes` then failed, leaving no shield over the embedded WeChat or WhatsApp area.
- Keep the shield visible and enabled over the native client while a JNest modal is open. Its first mouse click activates the JNest window group and is consumed before it can act on the chat; the deepest visible modal is then restored.
- Record Win32 error codes if shield creation, transparency or subclassing fails, alongside the existing click, Z-order and foreground diagnostics.

## Validation
Windows CI compiles the WPF Release build and runs ArchitectureProbe and BadgeProbe. The native shield's layer style and alpha setup were reproduced locally, but Chrome-to-client first-click behavior, nested dialogs and mixed DPI still require hands-on Windows acceptance.
