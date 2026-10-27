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


## v1.0.10 Final 微信登录角色识别修复候选（新独立 Draft PR）

此版本仅处理 **PR #59 诊断确认的主窗口误判为登录窗口**，不改变 DWM/Insets、窗口位置算法、窗口裁剪、WhatsApp、锁屏或退出流程。

### 根因与修复

固定 100% 冷启动的 PR #59 记录显示：真正的微信聊天主窗口 `1027x739`，然而旧版 Qt 通用窗口规则 `width*height < 1,000,000` 将其误标为 `Login`，进入 `CenterInHost` 居中布局。对于无法区分的 Qt 通用 `微信` 标题，改用 DPI 归一化后的**紧凑纵向/近方形登录窗形状**作为弱回退判据；专用登录标题及 `WeChatLoginWndForPC` 类名仍有最高优先级。

### 实机验收重点

1. 固定 100% 完全退出并重新启动微信和 JNest：**两个微信账号应显示“已接入”并铺满整个右侧工作区**，不再显示“等待登录”且聊天主界面居中。
2. 固定 200% 冷启动：两个微信和 WhatsApp 保持正常，账号历史、消息提醒和切换不受影响。
3. **真正未登录的微信登录/扫码页面仍须保持合适尺寸居中，不能被拉伸**；若出现真登录误判，应反馈截图，不能合并。
4. 100% 下最大化/还原 JNest，两个微信必须跟随右侧工作区；测试各账号切换和托盘恢复。
5. DPI 200% → 100% → 200%：此版**并未修复 PR #60 的缓存 Insets 问题**，灰带可能仍存在；仅验证主窗口不被重新降级为 Login。

请提供每个不同结果的一张截图。尚未进行实机验收之前，PR 必须保持 Draft，禁止合并或创建正式 Release。
