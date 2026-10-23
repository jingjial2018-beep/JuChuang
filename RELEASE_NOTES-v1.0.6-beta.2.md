# JNest v1.0.6-beta.2 — Modal foreground recovery and password UI

**Windows beta, unsigned.** The previous v1.0.6-beta.1 and stable v1.0.5 remain available.

## Fixed
- When a Settings/About/Exit dialog is open, switching to another app and clicking the exposed hosted WeChat or WhatsApp pane requests foreground activation for JNest's active dialog, rather than silently swallowing every click.
- Preserves the existing native-client input guard: the first click restores the dialog and does not operate the chat behind it.
- Nested password dialogs restore the deepest visible owner (Settings → change password → confirmation) instead of the disabled Settings window.
- Foreground activation is only triggered by an actual click; no global always-on-top behavior.

## Included from beta development branch
- Restore Microsoft YaHei UI and refine Settings scrollbar appearance.
- Unified password boxes (caret and password bullets vertically aligned, consistent corners), modal buttons and lock-screen styling.
- Password changing and password removal with old-password verification and a second confirmation. After removal, locking requests a new password.

## Validation notes
- WPF Release compilation, ArchitectureProbe and BadgeProbe must pass in Windows CI.
- Manual Windows testing is still required for focus behavior across Chrome, multiple WeChat windows, WhatsApp, scale factors and nested modal dialogs.
