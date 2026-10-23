# JNest v1.0.6-beta.1 — Source Han Sans and full-window password lock (preview)

**Prerelease — Windows integration testing required.** Stable v1.0.5 remains unchanged.

## Added
- Simplified Chinese Source Han Sans CN Regular and Medium as embedded WPF resources, pinned to the official Adobe release, checked against the official Git blob hash. UI typography normalized.
- Lock entire JNest content surface from main menu, not only the hosted-client panel. Titlebar remains available.
- Compact, centered 280-DIP password field and 108-DIP Unlock button using the existing JNest toolbar button style.
- Password creation (with confirmation), password change in Settings > 隐私与安全, PBKDF2-SHA256 salted password verification.
- Locked-state marker for JNest restart and simple repeated-password failure throttling.
- Park hosted native WeChat/WhatsApp windows through existing modal suppression; prevent background scans from reattaching/showing clients while locked.
- Toast notifications are generic while locked; clicking the Toast does not navigate into a chat until password verification, then checks stored process start time before choosing the account.
- Locked tray restore displays password entry instead of hosted-client content.

## Known scope and limitations
- Password lock applies to **JNest-managed surface**, not independent WeChat/WhatsApp windows previously popped out to Windows desktop.
- On a restart that begins locked, automatic window discovery is deliberately deferred until unlock to prevent a client from flashing during native attachment. Therefore JNest-originated Toast monitoring for new clients is limited until the first unlock.
- JNest is not a Windows desktop security boundary. Other apps' Toast notifications, task manager process access, or independently running clients are outside this protection.
- Needs real Windows 11 testing with multiple WeChat instances, WhatsApp, monitor scaling, media dialogs, system sleep, startup lock persistence, and actual notification activation.
- Windows build archives are unsigned.

## Fonts
Source Han Sans 2.005R, Adobe, licensed under SIL Open Font License 1.1. License is included under Assets/Fonts/LICENSE.txt. Sources: https://github.com/adobe-fonts/source-han-sans.
