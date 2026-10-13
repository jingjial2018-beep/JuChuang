## v0.3.17

本版本集中完成 v0.3.16 实测后本阶段需要完善的项目，不再拆分多个小版本。重点是修复最小化行为、补强微信 4.1.x 头像读取，并把“添加微信”升级为更确定的独立进程多开启动流程。

### 最小化与任务栏

- 修复聚窗最小化后从任务栏消失的问题。
- 最小化时不再调用 `Hide()`，聚窗继续保留 Windows 任务栏按钮。
- 被托管的微信 / WhatsApp 仍由 WindowHost 按原逻辑隐藏或停放，避免客户端窗口浮到桌面。
- 系统托盘图标继续保留；收到消息时不恢复、不抢前台，继续使用任务栏闪烁和托盘提示。

### 微信 4.1.x 头像读取

- 保留原有 `temp/head_image` 精确头像缓存匹配。
- 增加 Windows 微信 4.x 共享头像目录 `all_users/head_imgs` 的本地匹配。
- 增加 `db_storage/head_image/head_image.db` 当前账号头像 BLOB 回退：
  - 仅查询当前登录账号的头像行；
  - 不读取消息、会话或朋友圈数据库；
  - 不新增外部网络请求。
- 头像成功读取后继续进入聚窗自己的 Profile Cache，降低微信临时缓存变化带来的头像丢失。

### 微信多开启动器

- “添加微信”不再只依赖普通 `Process.Start`。
- 当已经存在微信进程时，优先采用与 EV 多聊同类的 Windows 单实例 Mutex 思路：
  - 定位 `XWeChat_App_Instance_Identity_Mutex_Name`；
  - 短时调整该 Mutex 的 DACL；
  - 使用 `CreateProcessW` 创建新的 Weixin / WeChat 进程；
  - 给新进程越过早期单实例检查的时间后恢复原 DACL；
  - 关闭聚窗持有的内核对象句柄。
- 不修改微信 EXE，不向微信进程注入 DLL，也不修改微信登录配置。
- 如果 Mutex 策略在某个微信版本上不可用，保留普通 `CreateProcessW` 回退，窗口发现与现有账号层仍继续工作。

### 精确 PID 绑定

- `CreateProcessW` 成功后直接取得新微信 PID。
- `ClientLaunchContext` 新增 ExpectedProcessId；对于权威 PID，窗口扫描只接受对应进程。
- 多微信同时运行时，降低“添加微信后误选中已有实例”的概率。
- ArchitectureProbe 增加精确 PID 绑定回归测试。

### 保持不变

- 不实现微信消息接管 / 静音，本版本继续保留微信原生消息行为。
- 不切换为 SetParent 子窗口架构。
- 不向微信进程注入 DLL，不 Hook 微信内部 API。
- 不修改微信本地登录配置或自动切换历史登录账号。
- 不读取聊天正文、消息数据库或会话数据库。
- 不改变 v0.3.16 已验证正常的未读数字 / 红点识别、账号代际隔离、Window Reconciler 与 WhatsApp 逻辑。

### 下载说明

- `JuChuang-v0.3.17-FrameworkDependent-win-x64.zip`
  - 轻量版
  - 需要已安装 .NET 8 Desktop Runtime x64
- `JuChuang-v0.3.17-SelfContained-win-x64.zip`
  - 免运行库版
  - 无需额外安装 .NET 运行库

支持 Windows 11 x64。本版本继续采用未签名发布策略，因此 Windows SmartScreen 首次运行时可能显示“未知发布者”提示。
