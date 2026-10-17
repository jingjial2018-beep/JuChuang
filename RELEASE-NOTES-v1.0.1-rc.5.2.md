# JNest v1.0.1-rc.5.2 — RM process identity layout fix

Fixes Restart Manager P/Invoke process structure alignment: native RM_UNIQUE_PROCESS is DWORD + FILETIME (12 bytes); previously the C# Int64 could be padded to 16 bytes. Uses explicit DWORD low/high fields and Pack=4; maintains strict PID + exact UTC start time comparison.

Improves per-account RM diagnostic details: SamePid, TimeMismatch, OtherPid, MaxDeltaMs, alongside existing ownership result and anonymous token. Same PID with mismatching start time remains inconclusive and cannot approve identity binding. No forced identity assignments.

Existing RC4 conservative fallback preserved. Windows CI checks builds, ArchitectureProbe, BadgeProbe; live RM success requires real-user validation. Does not yet implement ongoing RM ownership invalidation after login switch.
