# JNest v1.0.6-beta.3 — Modal dialogs and UI refinements

**Windows prerelease — unsigned.** Previous v1.0.6-beta.2 and stable v1.0.5 remain available.

## Changes
- Lock password setup now uses the same hosted-modal display path as Settings and About: WeChat / WhatsApp stay visible behind the dialog rather than showing a blank workspace.
- Reworked the first-click modal foreground recovery path for exposed native WeChat / WhatsApp workspaces: detect both hosted-client and disabled WPF owner clicks; queue foreground and non-topmost Z-order restoration of JNest and the active dialog. Keep the modal input guard so clicks do not operate underlying chat.
- Narrowed lock-screen password input to 275 DIPs with 9-DIP side padding; retain vertically centered password dots and caret.
- Centered confirmation/cancel buttons with 20-DIP spacing for new password, change password and password-removal confirmation dialogs. Dialog height now grows with content so buttons are not clipped.
- Password removal hint appears as a subtle placeholder inside the two empty new-password fields during password changes; it disappears as the user types. Actual password removal still requires old-password verification and a second explicit confirmation.
- Settings button wording shortened to “修改密码” (or “设置密码” when no password), with larger security-description text; scrollbar simplified to a narrow, quiet light-gray rounded thumb with no pronounced rail, arrows, gradient or shadow.
- Retained Microsoft YaHei UI, message alerts, account identity and native client hosting.

## Validation
- Windows CI checks WPF Release build, ArchitectureProbe and BadgeProbe before publication.
- Actual focus recovery after switching to Chrome, nested dialogs, on-screen placeholder alignment and native client interactions **still require hands-on Windows testing**. CI does not prove the first exposed-workspace click is fixed.

## Build
- Target: Windows x64, .NET 8
- Framework-dependent and self-contained ZIP archives are included.
