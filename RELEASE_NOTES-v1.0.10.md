# JNest v1.0.10 — 维护候选说明（未发布）

基于 v1.0.9 正式 Tag 的后续维护候选。只有通过 Windows 实机验收和手动官方发布工作流后，才可改为正式发布说明。

## 计划变更

- 缩减重复窗口跟踪日志，增加日志轮转及关键信息保留。
- 增强托盘退出和异步关闭时序防护，补充错误诊断。
- 修复单个客户端退出时底层 OS 请求长时间阻塞可能占用界面状态的问题。
- 设置页移除不适用于会话密码的修改/取消入口，侧边栏锁屏流程保持不变。
- 免打扰起止时间相同时明确提示全天静音。
- 移除无人使用的 AboutWindow，保留 AboutDialog。
- 修正文档并加入 200% DPI 人工视觉门禁。

## 验收要求

Build WPF Release、ArchitectureProbe、BadgeProbe、RegressionProbe 与设置排版检查通过；Windows x64 实机验证包括托盘退出、锁屏、微信/WhatsApp 托管与 200% DPI 截图。未完成前不得发布。

## Windows x64

计划保留 SelfContained 和 FrameworkDependent 两种 ZIP。正式 SHA-256 由发布工作流追加。
