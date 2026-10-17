# JNest v1.0.1-rc.1 — WeChat identity recovery test

This prerelease repairs the existing v1.0.1 release line. It does not replace the stable v1.0.1 release.

Source fixes since v1.0.1:
- Restore the stable v0.99 logic that excludes other active processes' verified account candidates prior to database validation.
- Prevent a disputed identity read from clearing a different active WeChat account's profile.
- Restore the stable login transition condition so unverified accounts are not reset on transient window role changes.
- Keep guarded PID/HWND checks for safe asynchronous profile binding and fast automatic profile retries.
- Reuse **扫描客户端并同步资料** for manual live profile refresh, instead of adding another button.
- Allow duplicate-login prompt discovery across all hosted WeChat states, not only entries currently classified Login.
- Keep expanded/collapsed logo alignment and existing WhatsApp hosting.

Validation required on a Windows PC with 2 different logged-in WeChat processes:
1. Close JNest but not WeChat. Restart JNest three times: verify each nickname/avatar and real chat ownership.
2. Check separate unread badges/Toast; re-login one account.
3. Try a duplicate login to verify the native "该账号已登录" prompt stays with the appropriate workspace.
4. Confirm sidebar layout, WhatsApp, manual scan+sync and client detach/reattach.

Windows CI tests only compilation and repository regression probes; live WeChat behavior is NOT guaranteed without manual validation.
