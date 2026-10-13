## v0.3.22

任务栏图标专项修复。

- 显式保留 JNest 主窗口的 Windows 任务栏按钮，并在窗口初始化和最小化状态变化时校验窗口扩展样式。
- 主窗口保持 `WS_EX_APPWINDOW`，移除可能导致任务栏按钮消失的 `WS_EX_TOOLWINDOW`；只有发现异常样式时才刷新非客户区。
- 继续使用 `Assets/app.ico` 作为主窗口、EXE 和托盘图标资源。
- 不修改微信/WhatsApp 窗口托管、消息角标、提示音或通知检测逻辑。

### 下载说明

- `JNest-v0.3.22-FrameworkDependent-win-x64.zip`：需要 .NET 8 Desktop Runtime x64。
- `JNest-v0.3.22-SelfContained-win-x64.zip`：包含运行库。

支持 Windows 11 x64，未进行商业代码签名。任务栏在不同 Windows 配置下的行为需安装后实际验证。
