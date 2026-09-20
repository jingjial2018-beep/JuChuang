## v0.3.12

本版本继续优化聚窗的页面化视觉托管，让微信与 WhatsApp 更接近聚窗自身页面，同时优先保持现有窗口管理机制的稳定性。

### 页面化与视觉改进

- 聚窗改为灰色导航/工具壳 + 连续白色客户端页面。
- 顶部壳层高度调整为 44px，左侧账号区调整为 252px，更接近微信式页面结构。
- 隐藏底部状态栏，客户端内容直接延伸到窗口底部。
- 右侧工作区保持零卡片、零留白、零额外边框。
- 接入微信或 WhatsApp 时关闭 Windows 11 DWM 圆角。
- 接入时关闭 DWM 系统边框颜色，进一步减弱独立悬浮窗口感。
- 客户端弹出时恢复其原生 DWM 圆角、边框等窗口外观。

### 稳定性策略

- 保留 `WS_CAPTION` 与 `WS_THICKFRAME`。
- 保留客户端顶层 HWND、现有 `SetWindowPos` 和直角 `SetWindowRgn` 托管方式。
- 不裁掉微信/WhatsApp 自身标题栏。
- 不改成 `SetParent + WS_CHILD`，降低输入、弹窗、DPI、多开及客户端升级后的回归风险。

### 下载说明

- `JuChuang-v0.3.12-FrameworkDependent-win-x64.zip`
  - 轻量版
  - 需要已安装 .NET 8 Desktop Runtime x64
- `JuChuang-v0.3.12-SelfContained-win-x64.zip`
  - 免运行库版
  - 无需额外安装 .NET 运行库

支持 Windows 11 x64。当前发布包未进行商业代码签名，Windows SmartScreen 首次运行时可能显示提示。