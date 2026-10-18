# JNest v1.0.3-rc.2 内部测试版

基于 v1.0.3-rc.1。

- “全部接入”可针对之前从侧栏关闭、进程仍存活的微信或 WhatsApp 主窗口执行定向恢复；核验 PID、进程启动时间及主窗口特征。普通扫描与后台扫描保持只发现可见窗口。
- 对客户端 HWND 被销毁或复用的情况增加进程归属和启动代际检查，避免沿用旧卡片身份或重复接入。
- Toast 激活优先按稳定账号身份定位；仅在进程启动代际匹配时使用 PID 辅助定位。未安全匹配时只前置 JNest；定位成功后撤回对应通知。
- 多实例激活的命名管道转发失败时，尝试前置现有主窗口；启动期激活请求排队处理。

测试包为 Windows x64 framework-dependent ZIP，运行前需安装 .NET 8 Desktop Runtime。Windows x64 构建、BadgeProbe 与 ArchitectureProbe 由发布工作流验证。微信 4.1.15.13、WhatsApp Desktop、多账号、混合 DPI、锁屏解除后 Toast 点击及系统勿扰行为仍需团队在真实桌面环境中手工验收。本测试版未签名。
