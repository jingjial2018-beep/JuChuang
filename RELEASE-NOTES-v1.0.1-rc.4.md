# JNest v1.0.1-rc.4 — AmbiguousAccountKey 状态修复测试版

基于 v1.0.1-rc.3。针对真实微信 insomnia. 同步后长期 Pending、诊断指出 multiple account databases matched this process 的问题。

- 将 AmbiguousAccountKey 显式映射为 CompatibilityIssue，而非 Pending。
- 诊断代码准确显示 AmbiguousAccountKey。
- 拒绝继承当前无法确认的昵称和头像，卡片明确提示“微信 · 身份待验证”。
- 保持原有多候选拒绝错绑、窗口接入、Logo、WhatsApp、未读角标及消息提醒路径不变。

**已知限制**：此版修复“无限同步中”状态与不安全身份展示，并未实现对多个匹配数据库的可靠独立身份裁决。insomnia. 可能仍无法自动恢复头像昵称。需要后续无敏感数据的身份验证机制与实机诊断。

CI 要求 Windows Release 编译及 ArchitectureProbe / BadgeProbe 通过后发布两个 x64 ZIP 包。
