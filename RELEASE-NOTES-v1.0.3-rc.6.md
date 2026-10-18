# JNest v1.0.3-rc.6 — WeChat reconnect diagnostics and matching dialogs

Based on v1.0.3-rc.5. Does not change reconnect, activation or modal behavioral decisions.

- About and Settings are both 540 x 392 WPF device-independent units (not literal screenshot pixels).
- Clicking "全部接入" starts a bounded three-minute diagnostic trace in `JNest-reconnect-diag.log` beside `JNest.exe`.
- Trace includes recovered/attached window handles, activation eligibility, selected HWND, foreground HWND and up to 100 hosted mouse samples, plus actual input-hook blocks.
- Existing `JNest-modal-zorder.log` remains unchanged.
- Trace may contain process IDs, HWNDs and screen coordinates, but no conversation text.
- Log writes are best effort: extract to a writable folder, not Program Files.

**Reproduction:** start RC6; close a WeChat account from sidebar; click 全部接入; immediately single-click some conversations and then double-click WeChat native caption to restore; share `JNest-reconnect-diag.log`.

This is a diagnostic prerelease, not a fix for the click issue. FrameworkDependent requires .NET 8 Desktop Runtime; SelfContained bundles it.
