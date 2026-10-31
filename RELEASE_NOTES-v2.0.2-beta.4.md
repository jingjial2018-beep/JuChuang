# JNest v2.0.2-beta.4 — Consolidated remediation preview

**Windows 11 x64 beta preview. Not merged into stable.**

## Changes

- H2/H3: 200-ms trailing native-location recheck and cleanup of stale per-HWND
  WinEvent throttling entries during detach/disappearance/disposal.
- N1: correct WinEvent registration failure reporting (`SetLastError`).
- DPI: observe `WM_DPICHANGED` and collect opt-in detailed snapshots of
  host/client DPI, native/DWM rectangles, host bounds, region and cached
  insets. Optional expert-only selected-window recalibration is disabled by
  default, gated by `JNEST_EXPERIMENTAL_DPI_RECALIBRATION=1`.
- W1: add a Settings opt-out for *new* WeChat local profile reads; previously
  confirmed identities and manual names remain usable. Existing automatic
  recognition stays enabled on upgrade to avoid breaking account binding.
- E5: fix RegressionProbe nullable warning; bump PR Actions generations to
  Node.js 24-compatible releases.
- Version + docs + ArchitectureProbe profile preference regression test.

## Test gates

PR Build Check, ArchitectureProbe, BadgeProbe, RegressionProbe, typography
validation and Windows x64 FrameworkDependent/SelfContained preview ZIPs.

Two WeChat and one WhatsApp; 100%/200% cold-start DPI; profile toggle;
modal dialogs; native/window interaction; tray and exit; and optional
200→100→200 diagnostic observations.

**Not fixed/guaranteed:** mixed DPI runtime hosting, all clients' arbitrary
Per-Monitor-V2 layout transitions, complete elimination of memory-resident
Win32 API hangs, and untested future WeChat versions. No stable merge.
