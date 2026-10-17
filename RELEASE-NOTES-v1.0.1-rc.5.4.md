# JNest v1.0.1-rc.5.4 — 微信重复登录弹窗归属专项

基于 RC5.3；针对现场诊断的 Qt51514QWindowIcon / Weixin / Owner HWND 关联，识别属于已接入微信登录主窗口的紧凑型原生弹窗。使用确切 Owner HWND 和相同 PID 验证，不凭模糊标题或其他账号 PID 猜测。定位于被选中账号的 JNest 右侧工作区，并保留多显示器屏幕边界保护。后台账号的已管理弹窗隐藏，返回时恢复定位。

保持 RC5.2 RM 验证、昵称头像、未读角标、WhatsApp 托管不变。

局限：仍按 1 秒宿主扫描轮询跟进位置，尚无原生弹窗特定自动化测试；CI 的 Windows 构建与现有 ArchitectureProbe/BadgeProbe 不等于实机弹窗回归通过。