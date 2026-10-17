# JNest v1.0.2 — Stable

This stable version is based on the RC5.6.2 popup DPI fix, which the user confirmed passed all eight native popup lifecycle regression tests. It unifies duplicate-login dialog management under ExternalWindowHost, handles physical window dimensions with GetDpiForWindow normalization before signature filtering, retains Owner/PID/Title/Class checks, and preserves native host window park behavior, notifications, account identity reading, and WhatsApp hosting. Diagnostic event logging remains available for troubleshooting.

Validation: GitHub Windows x64 build, ArchitectureProbe and BadgeProbe; user-confirmed live switching, restoration, window move/maximize/minimize, modal reappearance, process exit and application close. Existing v1.0.1 release is left untouched.
