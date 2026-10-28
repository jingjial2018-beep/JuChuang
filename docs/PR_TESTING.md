# JNest Pull Request 测试包说明

这份说明用于 GitHub Actions 的 **JNest PR test package (no release)** 预览构建。此压缩包仅供测试，不是正式 Release，不代表兼容性与安全性已经人工验收。

## 下载与运行

1. 在 GitHub 对应 Pull Request 的 **Checks** 中打开 **JNest PR test package (no release)**；也可以打开 Actions 中相应运行记录。
2. 下载 **Artifacts** 下的 `JNest-v...-preview-PR... `（内含 FrameworkDependent 和 SelfContained 两个 ZIP）。
3. 优先选择 SelfContained 自包含版；FrameworkDependent 版需要系统已安装 **.NET 10 Desktop Runtime x64**。
4. 将测试包解压到独立目录，勿覆盖正在使用的正式版本；运行 `JNest.exe`。
5. 测试完毕后退出测试版；正式版仍应从 GitHub [Releases](https://github.com/jingjial2018-beep/JuChuang/releases/latest) 获取。

## 手动回归关注项

- 微信 4.x 与 WhatsApp 窗口发现、接入、弹出、恢复和输入焦点。
- 账号选择、拖动排序、右键不误切换、昵称头像与未读角标。
- 设置、Toast、托盘、任务栏以及通知免打扰。
- 密码锁定、解锁与锁定状态下的退出；完整退出并重启后，旧会话密码及锁定状态不得沿用。
- 常规 DPI 和多显示器下的窗口尺寸、模态对话框与原生客户端交互。
- 测试软件退出是否按所选模式处理客户端进程。

自动编译与 ArchitectureProbe / BadgeProbe 通过**不能替代**原生微信／WhatsApp 的 Windows 实机验收。若测试失败，在对应 PR 中记录测试环境、复现步骤和已遮挡个人内容的截图。

## 完整性与安全

ZIP 同目录的 `SHA256SUMS.txt` 包含两种测试包的 SHA-256。请对下载的 ZIP 计算哈希后与该文件比对。

测试包未经商业代码签名，Windows SmartScreen 可能提示；请核实仓库来源，不要盲目忽略系统警告。JNest 可能在本地读取微信账号元数据，详细边界请看仓库 [README](../README.md)。

**禁止**把本测试 Artifact 重新上传为正式 GitHub Release，也不要覆盖已发布的正式版 Tag 或安装包。


## v2.0.0 后续维护候选验收

以下项目逐项记录 Windows 版本、微信和 WhatsApp 版本、显示器分辨率与缩放、构建提交、操作步骤、预期和实际结果。测试截图先遮挡账号及聊天内容。自动探针通过后仍须完成对应实机项；不通过不得合并或发布。

1. **.NET 10 / 两种 x64 包：** Release 编译、ArchitectureProbe、BadgeProbe、RegressionProbe 和设置字体检查全部通过；分别运行 FrameworkDependent（安装 .NET 10 Desktop Runtime x64）与 SelfContained 包。启动、WPF 对话框、Win32 托管、Toast 点击恢复均正常。
2. **临时资料安全：** 确认 `%LOCALAPPDATA%\JuChuang\tmp\profile` ACL 仅允许当前用户与 SYSTEM。人为制造目录权限设置失败时，资料读取应失败且不得创建明文副本。正常退出后无当前进程副本；强制结束后重启清理残留。
3. **资料容错：** 依次模拟无昵称、无头像、数据库不可读、密钥不确定和进程重启；不能把另一账号的昵称、头像、缓存或提醒绑定到当前窗口。恢复数据后应重新解析。
4. **角标性能与准确性：** 在前台、后台各运行 10 分钟，记录 CPU 峰值、内存和截图失败次数。验证微信数字、红点、清除确认、WhatsApp 聊天角标与通话角标；首次同步和数字未增长不得新增提醒。
5. **窗口与退出：** 分别测试“全部退出”“仅退出 JNest”、单客户端退出超时、托盘恢复、最小化、锁屏状态、异常关闭；微信与 WhatsApp 的 PID/HWND 不得错绑，未确认的进程退出不能显示为已完成。
6. **固定 150%：** 冷启动两个已登录微信，验证登录窗口、聊天主窗口、最大化/还原与 WhatsApp。无异常时不调整现有固定 DPI 托管算法。
7. **混合 DPI：** 两台显示器采用不同缩放，窗口在两屏之间移动、最小化、托盘恢复、弹出与重新接入；记录边框、灰带、点击坐标和模态窗口位置。
8. **运行中 DPI 改变：** 确认不弹出重启提示，也不新增 DPI 状态文字；记录窗口边框、裁剪和点击位置的实际表现。本候选不承诺实时原生窗口重排。

## 工程与发布评估

- 现有 Toast 使用 `Microsoft.Toolkit.Uwp.Notifications` 7.1.3；本候选保持现有激活和提醒规则。微软现推荐 WPF 使用 Windows App SDK 的 `AppNotificationManager`，迁移会触及注册和点击激活路径，应单独开发并做真实桌面回归（[官方说明](https://learn.microsoft.com/windows/apps/develop/notifications/)）。
- Windows 代码签名需要项目正式发布身份、可信证书及签名密钥保管方案；在这些条件确定前不修改发布工作流，不给预览包添加无效签名。
- `MainWindow` 已按通知、角标、锁屏、退出等职责拆为 partial 文件。本轮未发现必须再拆类才能安全修复的依赖，暂不重构窗口托管主路径。
