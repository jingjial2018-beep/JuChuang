# JNest v1.0.3-rc.7 — WeChat reconnect single-click candidate fix

Based on RC6. Narrow fix one only; Owner timing/recovery behavior intentionally unchanged pending real-device result.

- `AttachWindow` calls `ShowWindow(SW_RESTORE)` only with `makeActive=true`; background attach (`makeActive=false`) uses `SW_SHOWNA` to avoid foreground promotion.
- After explicit `全部接入`, selected hosted client is activated through the existing `WindowHost.ActivateCurrentWindow()` path. Existing foreground permission guard remains.
- Additional `JNest-reconnect-diag.log` entries show foreground HWND before/after activation and reason for skip.
- About/Settings unified at 540x392 WPF DIPs; RC5 modal z-order and Win+D behavior unchanged.

**Test:** close a WeChat via JNest sidebar, choose 全部接入, single click a conversation directly in the hosted main view without touching the titlebar. If it still fails, share `JNest-reconnect-diag.log` and describe whether the same problem occurs on both WeChat sessions. CI passing does not prove WeChat interaction on Windows.
