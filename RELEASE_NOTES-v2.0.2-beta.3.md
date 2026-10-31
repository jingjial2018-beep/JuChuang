# JNest v2.0.2-beta.3 — Stage C+D combined preview

Windows 11 x64 **preview only**; no official Release or stable merge.

## Stage C: database and activation security
- Recheck the decrypted scratch directory's effective ACL, owner and reparse
  constraints before creating plaintext; preserve creator-generation cleanup.
- Restrict legacy/scratch cleanup to JNest-owned filenames and avoid symlinks.
- Restrict activation Named Pipe to the current Windows user, bound input to
  2,048 characters, time out incomplete reads, and cap startup activation queue.

## Stage D: monitoring, logs and mouse hook
- Bound process-name/version caches while preserving existing HWND discovery rules.
- Throttle identical diagnostic exceptions to at most one entry per 30 seconds;
  keep existing 10-MB file/50-MB total/7-day limits.
- Fix timer handler cleanup and isolate native mouse-hook callback exceptions;
  retain hook delegate if native unhook fails.

## Validation
- Source build and all regression probes: see GitHub Actions attached to the
  Stage C+D Draft PR.
- Windows real-device validation remains mandatory: two WeChat + one WhatsApp,
  both 100% and 200% cold starts, and modal dialog/mouse-input behavior.
- Parent Stage B PR #65 has not been merged. Do not merge this stacked PR to
  `stable` without accepting earlier PRs in order.
