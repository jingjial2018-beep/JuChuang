# JNest v1.0.6-beta.5 — Modal click return and settings buttons

**Unsigned Windows beta.** v1.0.6-beta.4 remains available.

## Changes
- On opening a JNest modal dialog, place its temporary owned input shield above the visible native WeChat or WhatsApp client and keep the deepest dialog above the shield. The background layout timer only updates shield bounds; it does not raise JNest over other applications.
- Let Windows activate the shield on a real mouse click, consume that click, and then restore the deepest visible dialog. The click is never delivered to the chat client.
- Record first-click HWND hit, shield/client visibility, enabled state and Z order, plus foreground activation results in `JNest-reconnect-diag.log` for the first three minutes after a modal opens.
- Align the settings buttons with the switches. “修改密码” and “关闭” are both 104 × 36 DIP with 14 DIP text and the existing rounded shadow style.

## Validation
Windows CI compiles the WPF Release build and runs ArchitectureProbe and BadgeProbe. Actual foreground switching from Chrome to WeChat and WhatsApp, nested password dialogs and mixed DPI still require hands-on Windows acceptance. A passing CI run does not prove those interactions work on a user's desktop.
