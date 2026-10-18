# JNest v1.0.3-rc.1 — Combined RM arbitration and Toast activation

- Distinguishes confirmed Unowned account database from indeterminate Restart Manager failures; conservative Unique/Conflict/Unknown decision extracted as a pure function.
- Adds six ArchitectureProbe scenarios for historical accounts and error cases.
- Fixes misleading account conflict comments; no window hosting changes.
- Adds single-instance JNest mutex with named-pipe activation forwarding and Toast account identity/PID arguments; clicking a toast restores and foregrounds JNest and selects the matching account when found.
- Defers WhatsApp restart-card feature, global hotkey, per-account notifications to v1.1.0.

**Prerelease:** Automated CI verifies Windows x64 build, ArchitectureProbe and BadgeProbe. Toast clicks after process exit and historical account scenarios must be validated manually. v1.0.2 stable unchanged.
