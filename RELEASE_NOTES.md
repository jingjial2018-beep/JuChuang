## v0.3.14

本版本以 UI 精修和品牌更新为主，不改变 v0.3.13 已验证的微信 / WhatsApp 顶层窗口托管与消息检测架构。

### 新 Logo

- 使用新的聚窗品牌图标：深蓝圆角方形底 + 蓝绿窗格 / J 形标识。
- 同步替换窗口标题栏、程序 EXE、任务栏和系统托盘使用的应用图标资源。
- GitHub README 顶部品牌图同步更新。

### UI 精修

- 主界面字体栈改为 `Segoe UI Variable Text` 优先，中文回退 `Microsoft YaHei UI`，提升 Windows 11 下的清晰度。
- 顶部删除“一窗聚合多媒，矩阵高效出海”，只保留 Logo + “聚窗”。
- “关于”窗口删除同一句标语，托盘提示同步简化为“聚窗”。
- 顶部品牌区缩窄，为功能按钮释放更多空间。
- 账号卡片高度由 76px 增加到 84px，头像调整到 48px，改善拥挤感。
- 未读数字角标改为更稳定的圆形 / 胶囊结构，避免数字被压扁。
- 红点独立为 10px 圆点并提高显示层级。
- 修复鼠标移入有消息的账号后未读数字消失的问题；悬停现在只控制关闭按钮显示。

### 稳定性策略

- 不修改微信 / WhatsApp 的窗口发现和托管架构。
- 不修改 `AttachWindow`、DWM、输入、DPI、多开或消息检测逻辑。
- 保留 v0.3.13 的 WhatsApp 自动接入重试、账号级未读提醒和任务栏闪烁机制。
- PR 已通过 Windows Release 编译检查，主程序与 BadgeProbe 均构建成功。

### 下载说明

- `JuChuang-v0.3.14-FrameworkDependent-win-x64.zip`
  - 轻量版
  - 需要已安装 .NET 8 Desktop Runtime x64
- `JuChuang-v0.3.14-SelfContained-win-x64.zip`
  - 免运行库版
  - 无需额外安装 .NET 运行库

支持 Windows 11 x64。当前发布包未进行商业代码签名，Windows SmartScreen 首次运行时可能显示提示。