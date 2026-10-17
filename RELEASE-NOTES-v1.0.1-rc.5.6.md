# JNest v1.0.1-rc.5.6 — 微信原生弹窗管理器整合

基于 RC5.5.1，修复两处已确认的弹窗控制冲突：
- MainWindow.WeChatLoginPrompts.cs 的旧 SyncWeChatLoginPrompts/RestoreWeChatLoginPrompts 不再修改原生弹窗，仅保留空兼容入口。统一由 ExternalWindowHost 管理弹窗。
- ParkWindowOffScreen 调用 HideOwnedLoginDialog 时，已记录弹窗直接 SW_HIDE，不读取尚未更新的 _currentHandle。
- 新增弹窗 hide-park-new、hide-park-existing、hide-hook、show-restore 和 show-release 活动日志用于现场验证。

严格保持主微信窗口 WS_VISIBLE 屏幕外停放、任务栏通知、RM 身份识别、昵称头像及 WhatsApp 托管不变。已探针确认的微信原生窗口 Title=Weixin、Class=Qt51514QWindowIcon 识别签名保留。8 项实机回归未执行完成前不得判为验收通过。