# JNest v1.0.6 正式版

基于 v1.0.6-beta.7，整合左侧账号栏右键误切换修复及设置页面字体统一调整。

## 更新内容
- 左侧账号：左键切换微信/WhatsApp；右键点击其他账号、当前账号或空白位置不再改变选中账号；原有左键拖动排序保持不变。
- 设置页文字统一为微软雅黑 UI 四级字号：页面标题 21 DIP、分类标题 15 DIP、主文字与按钮 14 DIP、辅助说明/隐私说明/时间标签和时间框/底部保存提示 13 DIP。
- 五条辅助说明的字体颜色统一调整为 #596273，主文字与辅助说明间距调整为 3 DIP。
- 保留“修改密码”与“关闭”按钮 104×36 DIP 的大小和现有布局；不变更消息提醒、账号身份及微信/WhatsApp 托管逻辑。

## 自动化验证
Windows WPF Release 编译、ArchitectureProbe、BadgeProbe 及 UI 静态约束检查必须通过，才能发布正式安装包。

## 仍需实机检查
Windows 不同缩放比例下的字体显示、鼠标右键交互、从 Chrome 点击原生微信/WhatsApp 嵌入区域返回前台等行为仍需要用户在 Windows 上验收。CI 自动测试不等于已经通过所有原生窗口交互测试。旧正式版 v1.0.5 和 beta.7 不会覆盖或删除。

## 下载
- Windows x64 SelfContained：自包含版，无需另装 .NET 8 运行时
- Windows x64 FrameworkDependent：需要 .NET 8 Desktop Runtime
