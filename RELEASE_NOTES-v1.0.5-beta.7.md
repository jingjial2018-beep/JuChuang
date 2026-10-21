# JNest v1.0.5-beta.7

- Integrate more spacious Settings window, 540×482 DIP, keeping existing fonts, controls and settings logic.
- Prevent right-clicking an already open JNest tray popup from reopening or repositioning it.
- Observe external left/right mouse button transitions while the popup is open; outside clicks dismiss it without blocking other tray applications.
- Stop tracking when the popup closes and restore foreground/taskbar behavior when selecting '显示主窗口'.
- Retain beta.6 tray popup appearance, startup checkmark, icon assets and account hosting features.

**Testing note:** Windows UI automation cannot fully validate mouse interaction with the system tray overflow area; verify external clicks, other tray right-click, repeated right-click, and DPI/monitor behavior before promoting to stable.
