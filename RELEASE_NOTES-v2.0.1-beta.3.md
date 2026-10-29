# JNest v2.0.1-beta.3

Windows 11 x64 维护测试版。正式版仍为 v2.0.0。

## 本次修复

- 修复“全部退出”已结束所有微信进程、JNest 却仍留在桌面的情况。客户端退出确认后，JNest 会等当前窗口关闭事件结束，再执行自身关闭。
- 增加真实 WPF 窗口关闭事件的回归测试，验证延后关闭不会在同一个关闭事件内重入。
- 保留 beta.2 的长时间锁屏窗口恢复与弹出、重新接入修复。

## 下载与验收

- `SelfContained`：包含 .NET 10 运行时，推荐普通用户使用。
- `FrameworkDependent`：需要 .NET 10 Desktop Runtime x64。
- 两种 ZIP 均为未签名的 Windows x64 测试包。请分别验证“全部退出”和“仅退出 JNest”；长时间锁屏后的双微信恢复仍需按 `docs/PR_TESTING.md` 实机验收。
