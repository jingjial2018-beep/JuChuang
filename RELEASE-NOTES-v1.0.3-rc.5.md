# JNest v1.0.3-rc.5 — Modal Z-Order interception

Based on RC4 modal input guard. The About and Settings dialogs retain their owner, mouse guard, centering and non-draggable title bar.

- Intercept `WM_WINDOWPOSCHANGING` attempts to put either modal dialog into the global `HWND_TOPMOST` group.
- On `SourceInitialized`, check `WS_EX_TOPMOST` and clear an already-present state with `HWND_NOTOPMOST`.
- Log dialog creation, attempted TOPMOST promotion and close to `JNest-modal-zorder.log` in the same directory as `JNest.exe`.
- Hosted WeChat/WhatsApp windows remain visible; no global Topmost setting is added.
- The log is best-effort: if the JNest directory is not writable, logging will not be created; execution is unaffected.

Windows x64 test release. Validate switching to Chrome, Explorer and Win+D; check log for interceptions. FrameworkDependent requires .NET 8 Desktop Runtime, SelfContained bundles it.
