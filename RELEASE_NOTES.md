## v0.3.15

本版本为小幅 UI 布局调整，延续 v0.3.14 的稳定界面与 v0.3.13 的窗口托管、消息提醒架构。

### 顶部功能区

- 保持现有按钮样式不变。
- Logo + “聚窗”继续固定在左侧。
- “添加微信 / 添加 WhatsApp / 弹出窗口 / 全部接入 / 扫描客户端 / 关于”整组改为右对齐。
- 功能按钮组与最小化 / 最大化 / 关闭控制区保留约 18px 间隔。
- 使用自适应布局，不写死屏幕坐标；窗口尺寸变化时功能区保持右对齐。

### 稳定性策略

- 不修改微信 / WhatsApp 窗口发现与托管逻辑。
- 不修改消息检测、未读角标、DPI、多开、输入或 DWM 行为。
- PR 已通过 Windows Release 编译检查，主程序和 BadgeProbe 均构建成功。

### 下载说明

- `JuChuang-v0.3.15-FrameworkDependent-win-x64.zip`
  - 轻量版
  - 需要已安装 .NET 8 Desktop Runtime x64
- `JuChuang-v0.3.15-SelfContained-win-x64.zip`
  - 免运行库版
  - 无需额外安装 .NET 运行库

支持 Windows 11 x64。按照当前发布策略，本版本不进行商业代码签名，因此 Windows SmartScreen 首次运行时仍可能显示“未知发布者”提示。