# JNest v1.0.1-rc.5.5.1 — 微信原生弹窗恢复路径补丁

仅基于 RC5.5 修复两处漏掉的恢复调用：
- HandleOwnerWindowStateChanged(false) 在恢复当前主窗口后调用 RestoreOwnedLoginDialog(_currentHandle)，覆盖主窗体最小化 → 恢复。
- ResumeAfterModalDialog() 在 ScheduleActivation(_currentHandle) 后调用 RestoreOwnedLoginDialog(_currentHandle)，覆盖 JNest 自有模态窗口退出。

其他识别、按 Owner HWND 隔离、事件驱动防重显、后台主窗口屏幕外停放及消息闪烁、RM 身份识别、昵称头像未读数、WhatsApp 逻辑不变。

Windows x64 编译 / ArchitectureProbe / BadgeProbe 通过仅代表基础验证，必须在 Windows 实机完成规格中的 8 项回归测试才能验收。
