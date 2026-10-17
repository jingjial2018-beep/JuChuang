# JNest v1.0.1-rc.5.5 — 微信重复登录弹窗生命周期试验版

基于 RC5.4，在 ExternalWindowHost 主窗口 ParkWindowOffScreen 前查找并仅隐藏已验证的重复登录 Qt 弹窗。按 Owner HWND 独立保存 PID、HWND 和隐藏前 RECT；切回时先恢复微信主窗口，验证仍属同一进程和 Owner 后再显示弹窗；位置偏离宿主时仅重新居中一次。对后台已记录弹窗接入 EVENT_OBJECT_SHOW 防重显（事件驱动，无新增轮询）。保留主微信窗口 WS_VISIBLE，不改变其屏幕外停靠与消息闪烁路径，也不修改 RM、资料读取或 WhatsApp 逻辑。

必须实测：其他微信、WhatsApp、移动宿主、最大化/还原、最小化/恢复、关闭重开弹窗、微信进程退出、JNest 关闭共 8 项。CI Windows build / ArchitectureProbe / BadgeProbe 成功不等于 8 项实机验收完成。保留 RC5.4 可回退。