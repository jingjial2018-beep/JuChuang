# JNest v1.0.6 (stable)

Based on v1.0.6-beta.7 with a targeted account-sidebar right-click correction.

## Fixed in v1.0.6
- Right-clicking another WeChat or WhatsApp account card no longer changes the active account or embedded client. Both right-button down and up are consumed by the parent list before WPF ListBoxItem default selection.
- Left-click account switching, left-button dragging to reorder, and keyboard selection are retained.

## Included from the v1.0.6 betas
- Unified password lock, unlock, change and credential-removal confirmation design.
- Microsoft YaHei UI, Settings refinements, notification settings, and native-window hosting improvements.
- Work on modal foreground interaction when JNest owns separately hosted native client windows.

## Verification
- Automated Windows WPF Release compilation, ArchitectureProbe (including account input policy), and BadgeProbe run before publication.
- Real Windows acceptance is still necessary for right-click event routing, native client hosting, cross-app modal foreground recovery, and mixed-DPI setups.
- Prior betas reported an unresolved first-click foreground recovery case on exposed native chat areas; no independent hands-on test of that issue was performed for this stable release.
- Windows x64 ZIPs are unsigned.
