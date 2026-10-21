# JNest v1.0.5-beta.3 — UI acceptance build

This prerelease is based on v1.0.5-beta.2 and preserves the v1.0.4 stable release.

- Exit confirmation and JNest exit-choice dialogs: 104 DIP button widths with a 32 DIP horizontal gap at the standard 420 DIP dialog width; button rows stay centered and narrow dialogs retain stacked buttons.
- Client exit description: centered, naturally wrapped without forced breaks; original content and font sizes retained.
- JNest exit-choice descriptions: left-aligned in a centered text group; text wraps automatically.
- Pop-out menu PNG: 15% centered visual scale with the 20×20 DIP menu slot unchanged.
- Taskbar icon: separately generated multi-size ICO with 12% larger graphic footprint; large HWND icon is set without modifying the original app.ico, titlebar small icon, About, Settings, or notification resources.
- Existing account, process exit, hosted-window and message notification functionality is unchanged.

Windows taskbar icon rendering and tray/Alt-Tab behavior require visual acceptance on a Windows 11 machine. This release is not code-signed.
