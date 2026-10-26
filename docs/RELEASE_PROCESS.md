# JNest 正式版安全发布流程

当前唯一可信的后续开发与发布基线是 [stable](https://github.com/jingjial2018-beep/JuChuang/tree/stable)；截至 2026-10-26 已发布正式版为 [v1.0.9](https://github.com/jingjial2018-beep/JuChuang/releases/tag/v1.0.9)。

> 此文档说明以后如何发布。修改 CI 文档或执行验证，不等于用户批准发布新正式版。不得从旧 `main`、历史 `archive/*`、RC 分支或旧 Tag 发布。

## 一、开发与验收

1. 从 `stable` 新建独立 feature/fix 分支，完成代码与版本号变更。
2. 提交目标为 `stable` 的 PR，运行必需的 `verify-stable-baseline` Windows 编译、ArchitectureProbe 和 BadgeProbe。
3. 对涉及原生微信／WhatsApp 窗口托管、密码锁、通知或 DPI 的变更，要求用户实机验证后再合入 `stable`。
4. 发布前准备与项目版本一致的版本说明文件，例如 `RELEASE_NOTES-v1.0.10.md`；`JuChuang.csproj` 的 `Version` 必须是新的正式版版本号，例如 `1.0.10`。
5. 不得通过仅修改旧 `main` 工程版本号触发发布。

## 二、从 GitHub Actions 验证测试包（默认不会发布）

1. 打开仓库 **Actions**，选择 **JNest official release - manual only**。
2. 点击 **Run workflow**，**Branch 必须选择 `stable`**。
3. **operation** 选择 **validate**（默认）；`confirm_version` 留空。
4. 等待 Windows 构建、ArchitectureProbe、BadgeProbe 和 ZIP 打包完成。
5. 从该 Workflow Run 的 **Artifacts** 下载 `jnest-tested-candidate`，其中包含两种 Windows x64 ZIP、`SHA256SUMS.txt` 和本次说明。
6. 对下载后的测试包进行实机验收。仅验证时绝不创建或上传正式 Release。

## 三、正式发布（仅在用户明确批准后）

1. 确认新版本的 PR 已合并到 `stable`，`verify-stable-baseline` 已通过，用户已完成实机验收。
2. 在同一 Actions 工作流选择 **`stable`**、**operation = publish**。
3. 在 **confirm_version** 中**准确输入新版本 Tag**，例如 `v1.0.10`，再点击 Run workflow。
4. 工作流重新构建并检测：当前仍是 `stable` 最新提交、CI 已通过、版本号高于最新正式版、源码包含上一正式版、待发布 Tag 与 Release 均不存在、两份 ZIP 的 SHA-256 一致。
5. 检查全部成功后，工作流才会创建**新** GitHub Release 与 Tag；不会覆盖已有 Tag 或 ZIP。

> 当前 v1.0.9 已正式发布；发布新版本不得覆盖已有 Tag 或 Release。`republish` 是专门的显式更新路径，不应用于日常升级。

## 四、失败或测试版处理

- **构建失败**：回到功能分支修复，重新提交 PR；不要绕过检查。
- **Tag 或 Release 已存在**：不要使用 `--clobber`、`force push` 或重写正式版历史。核对已有版本，再决定是否创建新版本。
- **稳定分支在发布中发生变化**：自动停止，重新从最新 `stable` 验证。
- **Beta / RC 测试**：通过 PR 的独立验证工作流和 Artifacts 发给测试人员，**不使用正式版发布入口**。
- **历史工作流**：旧版 Release/Tag 保留作为记录，但旧发布脚本不得作为后续发行工具。不得从旧 `main` 或旧 Tag 手工发布。

GitHub Release 不等同于 Windows 实机兼容性验证；自动编译通过也不替代对原生客户端交互的人工验收。

## 五、200% DPI 人工视觉门禁（必需）

凡修改锁屏、弹窗、设置页或退出对话框，在 200% Windows 显示缩放下对首次密码设置、锁屏解锁、设置页、退出对话框、关于页和客户端退出确认框截图；逐项确认无文字裁剪、错位、换行异常或按钮内容溢出。PR 需附截图或验收记录；未通过不得发布。100% 缩放需完成基础复核。数值型 typography 脚本不能替代人工视觉检查。
