# JNest v2.0.2-beta.1

Windows 11 x64 maintenance preview. This is not a stable release.

## Changes

- Add an `IsHungAppWindow` pre-check before unread badge capture.
- Bound the badge scan caller's wait for native `PrintWindow` capture to 3 seconds.
- Add a per-HWND/PID in-flight gate and a 15-second cooldown after a timeout or unresponsive-window result, preventing repeated capture workers for the same client window.
- Keep timed-out, hung, skipped, and unavailable captures separate from “no unread”; they never clear an existing unread indicator.
- Add exception guards around window startup, periodic scan, unlock from button and Enter, delayed Shell reconciliation, and the sidebar client-exit event.
- Add BadgeProbe coverage for capture in-flight/cooldown isolation and RegressionProbe coverage for GuardedRunner failure handling.

## Validation

- CI Release build and probe results are recorded on PR #64.
- Real-device validation is still required with two WeChat accounts and one WhatsApp instance, including a temporarily unresponsive client and both 100% and 200% cold-start DPI settings.

## Known limitation

The 3-second timeout bounds the caller; it cannot cancel a native `PrintWindow` call already running. The in-flight gate prevents duplicate calls while that worker remains active. A hard termination boundary would require a separate helper process and is not part of this beta.

## Not included

No changes to the window-hosting algorithm, account identity binding, notification rules, exit strategy, WeChat database extraction, Named Pipe design, mouse-hook lifecycle, or runtime DPI switching.
