# JNest v2.0.2-beta.2

Windows 11 x64 maintenance preview. This is a test build, not a stable release.

## Changes

- Add a shared client-exit lifecycle for single-client exit and “全部退出”.
- Post `WM_CLOSE` to the previously verified client HWND first and allow a 2.5-second graceful-exit window.
- If the process remains alive, revalidate PID, process start time, and HWND ownership before a bounded `Kill(entireProcessTree: false)` fallback.
- Preserve account entries and report unconfirmed status when process identity or exit completion cannot be verified.
- Record whether each client exited gracefully or required forced termination.
- Update the bottom status bar to Chinese month/day and weekday, followed by “#” and 24-hour HH:mm; keep the existing 32-DIP bar height.

## Validation

- Build Check, ArchitectureProbe, RegressionProbe, and the preview package are run by GitHub Actions for the Stage B branch.
- RegressionProbe covers graceful success, Kill fallback, identity mismatch, Kill failure/timeout, and duplicate PID de-duplication.
- Real-device acceptance is required for single-client exit and “全部退出” with two WeChat accounts and one WhatsApp instance.

## Not included

No stable merge, official Release, window-hosting redesign, account identity redesign, database/Named Pipe security redesign, mouse-hook lifecycle redesign, or runtime DPI switching support.
