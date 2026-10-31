# JNest Pull Request 测试包说明

这份说明用于 GitHub Actions 的 **JNest PR test package (no release)** 预览构建。此压缩包仅供测试，不是正式 Release，不代表兼容性与安全性已经人工验收。

## 下载与运行

1. 在 GitHub 对应 Pull Request 的 **Checks** 中打开 **JNest PR test package (no release)**；也可以打开 Actions 中相应运行记录。
2. 下载 **Artifacts** 下名称为 `JNest-v...-preview-...` 的构建产物（内含 FrameworkDependent 和 SelfContained 两个 ZIP；PR 检查与 maintenance 分支 push 的命名后缀不同）。
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

> 说明：以下是 v2.0.0 历史维护候选的记录。当前 v2.0.2-beta.1 的 DPI 验收口径以文末 Stage A 的 100% / 200% 冷启动要求为准；运行中 DPI 切换不在当前范围。

以下项目逐项记录 Windows 版本、微信和 WhatsApp 版本、显示器分辨率与缩放、构建提交、操作步骤、预期和实际结果。测试截图先遮挡账号及聊天内容。自动探针通过后仍须完成对应实机项；不通过不得合并或发布。

1. **.NET 10 / 两种 x64 包：** Release 编译、ArchitectureProbe、BadgeProbe、RegressionProbe 和设置字体检查全部通过；分别运行 FrameworkDependent（安装 .NET 10 Desktop Runtime x64）与 SelfContained 包。启动、WPF 对话框、Win32 托管、Toast 点击恢复均正常。
2. **临时资料安全：** 确认 `%LOCALAPPDATA%\JuChuang\tmp\profile` ACL 仅允许当前用户与 SYSTEM。人为制造目录权限设置失败时，资料读取应失败且不得创建明文副本。正常退出后无当前进程副本；强制结束后重启清理残留。
3. **资料容错：** 依次模拟无昵称、无头像、数据库不可读、密钥不确定和进程重启；不能把另一账号的昵称、头像、缓存或提醒绑定到当前窗口。恢复数据后应重新解析。
4. **角标性能与准确性：** 在前台、后台各运行 10 分钟，记录 CPU 峰值、内存和截图失败次数。验证微信数字、红点、清除确认、WhatsApp 聊天角标与通话角标；首次同步和数字未增长不得新增提醒。
5. **窗口与退出：** 分别测试“全部退出”“仅退出 JNest”、单客户端退出超时、托盘恢复、最小化、锁屏状态、异常关闭；微信与 WhatsApp 的 PID/HWND 不得错绑，未确认的进程退出不能显示为已完成。
6. **固定 150%：** 冷启动两个已登录微信，验证登录窗口、聊天主窗口、最大化/还原与 WhatsApp。无异常时不调整现有固定 DPI 托管算法。
7. **混合 DPI：** 两台显示器采用不同缩放，窗口在两屏之间移动、最小化、托盘恢复、弹出与重新接入；记录边框、灰带、点击坐标和模态窗口位置。
8. **运行中 DPI 改变：** 确认不弹出重启提示，也不新增 DPI 状态文字；记录窗口边框、裁剪和点击位置的实际表现。本候选不承诺实时原生窗口重排。
9. **长时间 Windows 锁屏恢复：** 固定 200% 缩放、两个已登录微信，分别以前台和后台微信锁屏过夜。解锁后检查内容边缘与输入；将异常微信弹出，确认桌面及任务栏可找到原生窗口；重新接入后确认有画面，再选择“仅退出 JNest”确认微信仍可独立显示。记录 PID、HWND、锁屏时长、每一步的实际结果与 `host-*` 诊断事件。

## 工程与发布评估

- 现有 Toast 使用 `Microsoft.Toolkit.Uwp.Notifications` 7.1.3；本候选保持现有激活和提醒规则。微软现推荐 WPF 使用 Windows App SDK 的 `AppNotificationManager`，迁移会触及注册和点击激活路径，应单独开发并做真实桌面回归（[官方说明](https://learn.microsoft.com/windows/apps/develop/notifications/)）。
- Windows 代码签名需要项目正式发布身份、可信证书及签名密钥保管方案；在这些条件确定前不修改发布工作流，不给预览包添加无效签名。
- `MainWindow` 已按通知、角标、锁屏、退出等职责拆为 partial 文件。本轮未发现必须再拆类才能安全修复的依赖，暂不重构窗口托管主路径。


## v2.0.2-beta.1 Stage A — 角标截图与异常隔离验收

本节适用于 `v2.0.2-beta.1` 维护测试包。请先记录 Windows 版本、JNest 构建提交、微信/WhatsApp 版本、显示器分辨率与缩放。

1. **基础运行：** 使用两个微信账号和一个 WhatsApp 启动 JNest。确认三个窗口均可正常接入、切换、弹出与重新接入；锁屏、托盘和任务栏行为不变。
2. **正常未读提醒：** 分别验证微信数字角标、红点、WhatsApp 聊天角标。新增未读仍按现有规则提醒；角标消失仍需连续两次确认；截图不可用不得被当成“无未读”。
3. **无响应隔离：** 若能安全复现某个客户端进入 Windows “未响应（Not Responding）”状态，观察 JNest 主界面是否仍可操作、其他账号是否仍可切换。无响应客户端已有的数字/红点不得因截图超时而清零。不要为了测试而结束其他账号进程。
4. **恢复重试：** 客户端恢复响应后，等待原生截图任务结束及冷却时间到期，确认角标扫描能够恢复。不要要求立即重试：超时后有 15 秒冷却；若原生调用仍未结束，同一 HWND/PID 不会启动第二个截图任务。
5. **异常日志：** 如发生无响应或截图超时，检查 `%LOCALAPPDATA%\JuChuang\logs\diagnostics-YYYYMMDD.log`，留意 `badge-window-hung`、`badge-capture-timeout` 和 `badge-scan-unavailable`。分享日志前请遮挡个人路径及账号相关内容。
6. **DPI 冷启动：** 分别在 Windows 100% 和 200% 缩放下完全退出 JNest 后重新启动，检查窗口托管、账号切换、角标和 WhatsApp 显示。运行中切换 DPI 不属于本次验收范围。

**判定标准：** 一个客户端截图异常不能清除其现有未读状态、不能使 JNest 主界面卡死，也不能造成同一 HWND/PID 的重复截图任务；其他客户端的托管和通知规则保持不变。

**软超时说明：** 3 秒只限制 JNest 等待截图结果的时间，不能终止已进入 Win32 的 `PrintWindow` 调用。若原生调用永久不返回，该窗口会保持 in-flight 状态，直到该工作线程实际结束；真正硬终止需要独立辅助进程，不属于 beta.1。


## v2.0.2-beta.2 Stage B — graceful exit and Kill fallback

1. **Single-client exit:** select one expendable WeChat or WhatsApp client, confirm exit, and verify that JNest first requests normal window close. The account entry is removed only after process exit is confirmed.
2. **All-client exit:** with two WeChat accounts and one WhatsApp instance connected, choose “全部退出”. All unique client PIDs should receive the graceful-close request without one client serially blocking the others. JNest closes only after all target processes are confirmed exited.
3. **Grace period and fallback:** a client that remains alive after the 2.5-second grace period may be force-terminated. Use a disposable session and save any work first; do not test forced termination with unsaved content.
4. **Identity protection:** before graceful close and again before Kill, the process PID and start time must match the captured process generation. If the original HWND still exists, it must still belong to that PID. A changed/reused HWND or PID must block Kill.
5. **Unconfirmed result:** if a request or process-exit confirmation times out, JNest must not show success or remove the account entry. Automatic scanning may reconcile the entry later.
6. **Status bar:** confirm the bottom display reads Chinese month/day and weekday, then “#” and 24-hour HH:mm (no seconds). The 32-DIP status bar height and its other controls remain unchanged.

**判定标准：** graceful close is always attempted first; Kill is only a fallback after the bounded grace period and a fresh identity check. No unrelated process tree is terminated, and no unconfirmed exit is reported as successful. Stage A's accepted two-WeChat/one-WhatsApp behavior and 100%/200% cold-start DPI behavior must remain unchanged.


## v2.0.2-beta.3 Stage C+D — security and monitoring

**Use a separate installation directory and avoid production accounts/data
for destructive security testing.** Retain the Stage B exit checks.

1. With two WeChat accounts plus one WhatsApp, verify metadata and avatars
   remain correct, including after restarting one client. The SQLCipher scratch
   directory must have protected permissions limited to the current user and SYSTEM.
   No decrypted database must remain after normal completed reads.
2. Run a second JNest instance in the *same Windows user session*: it should
   activate the first instance. Windows Toast click-to-account should still work.
   Do not send test commands from an untrusted or another-user session.
3. If a peer connects to the pipe but sends nothing, it must not permanently
   block later activation requests. The 3-second deadline should free the reader.
4. Exercise login-to-main, HWND recreation, maximize/restore, hide/show, and
   account switching. Client discovery must remain correct after many short-lived
   unrelated desktop processes.
5. Repeatedly open and close nested modal dialogs, including the password
   confirmation flow, and verify mouse clicks in unrelated applications still
   work. Check that no modal guard traps input after closing JNest.
6. Run with diagnostics enabled for targeted observation; verify 10-MB
   rotation/50-MB total/7-day retention behavior. Identical exceptions within
   30 seconds should not flood the files; distinct failures should still log.
7. Verify 100% and 200% cold starts independently. Runtime DPI switching
   remains out of scope.

**Stop conditions:** inability to read the user's own profile database due to
ACL failure, any unexpected access by another Windows user, incorrect account
binding, a stuck activation pipe, or mouse input blocked outside the hosted
modal context. Do not merge pending an investigation.


## v2.0.2-beta.4 — consolidated remediation preview

Preview only, stacked on the Stage C+D beta.3 branch. Never merge to stable
without real-device acceptance.

1. Confirm 100%/200% cold-start hosting of two WeChat + one WhatsApp,
   including badge updates, notification, lock/unlock, modal dialogs,
   pop-out/reattach and client exit.
2. Exercise repeated WinEvent location bursts while resizing/restoring
   hosted windows. Trailing 200-ms rechecks must not cause excess movement
   or block input in unrelated applications.
3. Under Settings → 微信本地资料 disable automatic local profile reads,
   restart, and verify manual labels and existing verified identities are
   preserved while no *new* profile reads start. Re-enable to resume
   resolution of previously unresolved accounts.
4. Launch with `JNEST_DIAGNOSTIC_DETAIL=1`. Change DPI 200→100→200 and
   examine `host-dpi-snapshot` (host/client DPI, window/frame/host/region
   rectangles and cached insets). Read-only observations are the default;
   they do **not** imply runtime DPI compatibility.
5. Expert-only: `JNEST_EXPERIMENTAL_DPI_RECALIBRATION=1` enables guarded
   selected-window insets remeasurement. Stop on any new clipping or focus
   regression and relaunch without this variable. This is not the
   production acceptance path.
6. Confirm Windows Build Check, ArchitectureProbe, BadgeProbe,
   RegressionProbe, typography validation and both x64 ZIP modes.

The official `stable` baseline remains unchanged.
