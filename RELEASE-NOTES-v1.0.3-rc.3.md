# JNest v1.0.3-rc.3 — About / Settings modal fix

Based on v1.0.3-rc.2. This prerelease fixes the About and Settings modal dialog interactions:
- Both dialogs use a shared hosted-modal lifecycle; native WeChat / WhatsApp windows are parked during dialogs and restored afterward.
- Dialogs are centered on the actual JNest window bounds, including expanded sidebar, collapsed sidebar, and maximized window states.
- Prevent caption dragging while retaining close buttons and original dialog settings/appearance.
- No changes to RM identity, Toast, notification badge, or client recovery business logic.

Windows x64 Release, ArchitectureProbe and BadgeProbe are run by GitHub Actions. Real-machine modal behavior across DPI / client states needs verification.

Unsigned internal-use test build. FrameworkDependent needs .NET 8 Desktop Runtime; SelfContained includes the runtime.
