# JNest RC5.1 Restart Manager diagnostics

Based on RC5. Adds per-account RM ownership status, matched anonymous account token and diagnostic code to copied account report. Improves separation of definitively other-owned databases vs unavailable probes. Still retains safe RC4 fallback for uncertain ownership. This is not yet a full process-switch identity-lifecycle solution or a separate profile-reading pipeline. Requires Windows x64 build, ArchitectureProbe and BadgeProbe; live Windows RM behavior must be tested by user.
