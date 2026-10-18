# JNest v1.0.3-rc.11 — Client exit modes (RC6 baseline)

This release starts from the **RC6** code line and does not include RC7/RC8 window activation/Owner experiments.

Changes:
- Left sidebar account close now asks for confirmation and attempts to terminate only the validated client PID, rather than sending WM_CLOSE which can hide to tray.
- Before termination it checks HWND/PID ownership and the process creation timestamp.
- The close dialog uses a compact warning layout with amber triangle icon.
- JNest top-right X now offers **仅退出 JNest**, **全部退出** and **取消**.
- JNest-only exit preserves RC6's DetachAll behavior to restore native client windows.
- Exit-all requires a second confirmation and checks currently hosted client PIDs before requesting termination.
- Retains RC6 diagnostics: JNest-reconnect-diag.log and related logs.

Warnings: Process.Kill is a forceful termination and may lose unsaved data. If some PIDs cannot be verified or terminated, all-exit remains in JNest and reports the error. A partial termination may have occurred before the failure; retry only after inspection. The dialog is a compact WPF implementation; sizing/style need Windows visual verification.

Validation requested: test independent WeChat sessions and WhatsApp, left-account close, JNest-only close, all-exit including rejection, invalid PID, and single-click behavior on a fresh launched WeChat. CI build and probes are not substitutes for GUI integration tests.
