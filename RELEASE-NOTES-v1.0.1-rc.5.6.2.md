# JNest RC5.6.2 — DPI-aware WeChat popup recognition

Changes only `ExternalWindowHost` popup size recognition: calls existing `NativeMethods.GetDpiForWindow(popup)` and compares width/height in 96-DPI logical units; original RECT remains unchanged for restoration. Logs measured window DPI and normalized size. Identity checks (PID/Owner/Title/Class), lifecycle logic, RM, badges and WhatsApp remain as RC5.6.1.

Diagnostic-only evidence on RC5.6.1: target popup 660x438 failed `size-mismatch`. At DPI 192 that is 330x219 and should MATCH. Windows x64 build and probes do not establish real-world compatibility. Validate eight manual scenarios.
