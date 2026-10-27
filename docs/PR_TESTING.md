# JNest Pull Request 测试包说明

这份说明用于 GitHub Actions 的 **JNest PR test package (no release)** 预览构建。此压缩包仅供测试，不是正式 Release，不代表兼容性与安全性已经人工验收。

## 下载与运行

1. 在 GitHub 对应 Pull Request 的 **Checks** 中打开 **JNest PR test package (no release)**；也可以打开 Actions 中相应运行记录。
2. 下载 **Artifacts** 下的 `JNest-v...-preview-PR... `（内含 FrameworkDependent 和 SelfContained 两个 ZIP）。
3. 优先选择 SelfContained 自包含版；FrameworkDependent 版需要系统已安装 **.NET 8 Desktop Runtime x64**。
4. 将测试包解压到独立目录，勿覆盖正在使用的正式版本；运行 `JNest.exe`。
5. 测试完毕后退出测试版；正式版仍应从 GitHub [Releases](https://github.com/jingjial2018-beep/JuChuang/releases/latest) 获取。

## 手动回归关注项

- 微信 4.x 与 WhatsApp 窗口发现、接入、弹出、恢复和输入焦点。
- 账号选择、拖动排序、右键不误切换、昵称头像与未读角标。
- 设置、Toast、托盘、任务栏以及通知免打扰。
- 密码锁定、程序重启后锁定状态、解锁与锁定状态下的退出。
- 常规 DPI 和多显示器下的窗口尺寸、模态对话框与原生客户端交互。
- 测试软件退出是否按所选模式处理客户端进程。

自动编译与 ArchitectureProbe / BadgeProbe 通过**不能替代**原生微信／WhatsApp 的 Windows 实机验收。若测试失败，在对应 PR 中记录测试环境、复现步骤和已遮挡个人内容的截图。

## 完整性与安全

ZIP 同目录的 `SHA256SUMS.txt` 包含两种测试包的 SHA-256。请对下载的 ZIP 计算哈希后与该文件比对。

测试包未经商业代码签名，Windows SmartScreen 可能提示；请核实仓库来源，不要盲目忽略系统警告。JNest 可能在本地读取微信账号元数据，详细边界请看仓库 [README](../README.md)。

**禁止**把本测试 Artifact 重新上传为正式 GitHub Release，也不要覆盖已发布的正式版 Tag 或安装包。


## v1.0.10 Final Frame Insets 专项修复候选（PR #60）

此候选严格从正式版 `v1.0.10 Final` 建立新分支，不包含 PR #57 实验内容。

根据 PR #59 对比：200% 下 DWM frame 相对于 HWND 内缩 `11,0,11,11`，100% 下 frame 与 HWND 完全重合，但宿主仍保存 `11,0,11,11`。这会让原生可见 frame 在 100% 下溢出工作区边界。

此版本只添加以下机制：
- 对于**当前显示的正常大小客户端**，一秒一次读取 HWND 和 DWM frame 的物理像素偏移；
- **连续两次稳定**且合法（0–20 px）的测量结果与旧缓存不同时，重设 Insets 并重新布局；
- 不修改主窗口主题、自绘边框、原生圆角、WhatsApp 停靠、微信登录小窗逻辑；
- 记录 `host-frame-insets-corrected` 事件，无账号/消息内容。

请在单独目录解压，不覆盖正式版。分别以 200%（固定启动）及 100%（固定启动）验证两个微信账号、WhatsApp，再运行中切换 200%→100%→200%（每个状态至少保持 4 秒），对比左侧灰带、顶部和底部以及切换闪烁。有反常请上传截图和 `%LOCALAPPDATA%\JuChuang\logs` 下含 `host-frame-insets-corrected` 的日志。

**本分支仅供实机验收，不允许直接合并或创建正式 Release。**
