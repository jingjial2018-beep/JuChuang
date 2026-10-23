# JNest v1.0.6-beta.4 — Native hosted-client modal activation

**Unsigned Windows beta.** v1.0.6-beta.3 and prior versions remain available.

## Changes
- Temporary native click shield over visible hosted WeChat/WhatsApp working area during owned modal dialogs. First click attempts to return JNest and topmost dialog without interacting with underlying chat.
- Synchronous activation fallback from the mouse guard, with no global always-on-top mode and no DLL injection.
- Settings footer wording remains “设置即时生效并自动保存”, now 13 DIP.
- Top privacy statement 12.5 → 13 DIP.
- Lock subtitle 14 → 16 DIP; unlock button text 13 → 15 DIP; background reminder 12 → 14 DIP.
- Preserve established account fonts: nickname 14 DIP, client version/status 13 DIP, menu 13.5 DIP, status counts 13 DIP.

## Testing
Windows CI must pass WPF Release compilation, ArchitectureProbe, and BadgeProbe before publication. Windows hands-on testing remains necessary for actual first-click return, nested dialogs, DPI, and native WeChat/WhatsApp interactions.
