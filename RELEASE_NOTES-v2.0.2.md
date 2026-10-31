# JNest v2.0.2

This maintenance candidate contains the stacked changes from PR #63 through #67
and the four user-approved corrections. It must pass Windows CI and controlled
real-device acceptance before an official release is created from `stable`.

## Changes

- Status bar: Chinese month/day, short weekday and time with seconds, separated
  only by spacing. The status bar remains 32 DIP high with unified typography.
- Account ×: an embedded client follows the confirmed client-exit flow. A
  manually popped-out client is removed only from the JNest list and continues
  running independently. Background scans do not immediately reattach it;
  “全部接入” can restore it.
- WeChat nickname and avatar recognition stays automatic. The beta.4 profile
  preference and its test/documentation instructions are removed; existing
  profile-read privacy protections remain.
- Lock password dialog has tighter vertical spacing and no reserved error area
  when there is no error. Password fields, buttons and verification stay intact.

The inherited maintenance stages also contain .NET 10 migration, safer profile
scratch-file handling, bounded unread screenshot work, guarded asynchronous
events, process-generation-checked graceful exit with bounded Kill fallback,
same-user activation pipe limits, monitoring and diagnostics hardening, and
WinEvent/DPI diagnostics. Runtime DPI switching remains experimental and is
not guaranteed.

## Release acceptance

Build Check, ArchitectureProbe, BadgeProbe, RegressionProbe, settings typography
validation and both Windows x64 publish modes must pass. Complete the real-device
matrix in `docs/PR_TESTING.md` for two WeChat instances and WhatsApp at 100% and
200% cold starts, including account × in both states, “全部接入”, lock/unlock,
notifications, modal input, window recovery and privacy behavior.
