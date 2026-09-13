# OcctSharp 布尔历史接入

此目录保留 H2-B2 的上游源码、测试和可审阅补丁。实际代码位于相邻 OcctSharp 仓库；Cadoryx 仅通过 NuGet 消费，不引用上游工程或添加 OCCT P/Invoke。

`upstream.patch` 与 `changes.json` 记录该批改动；`Apply.ps1 -CheckOnly` 检查全部原文件哈希，`Apply.ps1` 在检查通过后应用。已应用的完全相同内容会跳过，其他变化会拒绝覆盖。它不修改生成目录、Git 元数据或发布任何内容。应用路径默认为 `C:\Users\yoiri\source\repos\OcctSharp`，位于 Cadoryx 工作区之外，受执行环境权限控制。

`prepare.py` 用于最初在未修改的上游基线上生成补丁；后续实际实现与验证记录由 `capture.py` 从既定文件列表重新捕获，不能用旧草稿覆盖当前源码。源码基线为 Preview.28；上游完整产品发布状态仍由其 STATUS.md 管理。

依次编译上游 Release／Debug 原生桥、运行 BooleanTopologyHistoryTests，再运行 `Verify-Abi.cmd` 与 `Verify-Native.ps1`。前者检查 C11 函数签名及固定布局；后者核对新增唯一导出、原导出无删除、两种配置导出一致，以及测试目录全部 62 个 DLL 与实际构建的哈希一致。

`Build-LocalPackages.ps1` 从实际 Release 输出创建 `8.0.1-preview.28.cadoryx.h2b2.2` 的 14 个本地开发 NuGet，保留正式 Preview.28 和早期开发包。`Consumer/Consumer.csproj` 仅引用 Modeling／Native 包，用已知体积和拆分关系验证独立加载，核对实际原生桥哈希。Cadoryx 的 Directory.Build.props 和锁文件固定同一版本族。

完整应用验收使用 `scripts/verify.ps1 -PublishSmoke -WindowSmoke -RecoverySmoke`。如果执行被中断，可用 `Verify-Published.ps1 -PublishDirectory <已验证发布目录>` 单独重跑窗口与恢复；它不重新构建，结果仅适用于传入的那份产物。桌面进程使用隔离目录和受限 PATH，恢复仅终止本次启动的诊断进程。

当前实现和能力边界见[布尔历史](../../docs/BOOLEAN_HISTORY.md)，实际通过项与未运行门禁见[路线图](../../docs/ROADMAP.md)。
