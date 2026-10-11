# 视口故障清理与恢复边界

2026-10-10。用户要求完成 A–F，G 实机矩阵、安装和交付暂缓。本项仍属于 A5 的交互稳定性与渲染故障工作。

## 已复现的问题

使用已验缓存修复版 1000 / SDK26，在探针自己创建的隐藏窗口内关闭自己持有的原生 `OcctViewer`，保留原始三个场景，再增加两个已完成场景的关闭检查。它模拟视口所有权失效，不是物理 GPU 设备丢失，没有操作桌面键鼠或用户文档。

| 场景 | 修复前实际结果 | 修复候选实际结果 |
| --- | --- | --- |
| 第二个几何资产缺失 | 原始缺资产异常保留，最终资产 0 | 相同原始异常保留，最终资产 0 |
| 渐进普通实体失去原生上下文 | 追加错误被 Abort 重绘错误覆盖，随后 Dispose 也失败 | 原始追加错误保留，Dispose 成功，全部资源与资产 0 |
| 渐进来源样式失去原生上下文 | 追加错误被 Abort 重绘错误覆盖，随后 Dispose 也失败 | 原始追加错误保留，Dispose 成功，全部资源与资产 0 |
| 已完成普通场景后直接关闭失效上下文 | Dispose 在 ClearSolidHandles 重绘处失败，剩 1 份资产 / 2,540 字节 | Dispose 成功，全部资源与资产 0 |
| 已完成来源样式场景后直接关闭失效上下文 | Dispose 在 ClearSolidHandles 重绘处失败，剩 2 份资产 / 7,557 字节 | Dispose 成功，全部资源与资产 0 |

修复前后消费程序的源码、五个场景和断言逐字节相同，仅替换修复后的 Renderer DLL、符号文件及预期哈希。实际 17 应用 + 13 SDK 托管与 62 原生库全部绑定。见[修复前五项](../artifacts/a5-native-context-cleanup-probe-draft-2/runtime-1-verification.json)及[修复后同一消费程序](../artifacts/a5-viewport-cleanup-repair-draft-1/consumer-runtime-1-verification.json)。

## 修复实现

候选在渐进追加、完成失败时重新抛出原始异常，将后续清理错误存入原始异常的 `Data["Cadoryx.Rendering.CleanupFailures"]`。Abort、缓存裁剪和最终关闭尝试释放全部资源；原生对象释放失败不能跳过后续形状与资产租约释放。最终关闭先标记已关闭，避免清理句柄时再次重绘失效上下文；清理过程中发现的真实错误仍在所有资源尝试完成后抛出。

修改涉及原 Renderer 的五个文件和一个新增清理辅助文件。独立 Renderer 修订 2 构建为零警告、零错误。第一轮错误引用了整套 WPF 发布载荷，产生依赖冲突及重复程序集的构建失败，已保留；修订 2 只使用必需 CAD / SDK 引用，修复代码逐字节不改。见[修订 2 来源](../artifacts/a5-viewport-cleanup-repair-draft-1/preparation-2.json)和[消费载荷核对](../artifacts/a5-viewport-cleanup-repair-draft-1/consumer-preparation-1.json)。

当前修复尚未应用到生产。已将它与直接 A–F F5 修订合并到独立工作区，保留原 5,998 个测试、所有预算和 SDK26；两配置构建均零警告、零错误，各 89 项专项和 656 项相关回归通过，相关清单保留原 646 项并增加 10 项。完整回归首轮中断，无终态或 TRX，原因未明；同一冻结源码与载荷的第 2 轮已启动，尚无完整结论。见[隔离集成来源](../artifacts/a5-cleanup-f5-integrated-draft-1/preparation-1.json)、[两配置相关验证](../artifacts/a5-cleanup-f5-integrated-draft-1/build-related-both-1-verification.json)及[首轮中断记录](../artifacts/a5-cleanup-f5-integrated-draft-1/full5998-first-interruption-1.json)。

## 当前完成与仍须验证

相同 F5/Renderer 候选完整 Release、Debug 各 5,998 项通过，原条件两组桌面窗口和 60 张完整图像通过。六项真实文档/历史/生产存储/新视口重建及 24 张完整图像也通过，见[统一接续](A5_UNIFIED_MATERIAL_RECOVERY_PLAN.md)。它们不证明物理 GPU 失效或自动 WPF 恢复。

后续实际 HwndHost 观察复现了独立问题：已关闭 viewer 下取消输入阻断销毁、保留窗口和资产并覆盖回调异常。统一 workspace-3 的修复通过相同已编译四项探针以及真实 Pane/VM 十二项拆卸路径；两配置构建与各 200 专项、789 相关通过。当前完整 6,109 项 Release→Debug 已启动，尚未取得最终两配置通过。

仍需统一候选的材质/纹理故障、生产界面和受影响窗口验收；物理图形驱动/设备丢失、其他渲染失败和自动恢复矩阵继续。原 MiniPC3 两次全新进程、各 20 双视口周期、6,482 毫秒目标、完整画面与原时间预算保持，生产第二轮 6,800.2534 毫秒失败保留。Escape 中止轮保持未通过，不把后来独立通过改写为原中止轮通过；后续每次实际桌面键鼠开始前提示。

SDK26 公开显示函数的三个小型对比还确认：普通实体、共享实体、来源样式实例的默认画面均与显式线框完整字节相同，与显式着色不同。9 张完整图像已取得，不能据此宣布大模型提速；后续需验证能否直接以所需模式显示对象，保持现有 API 默认行为、来源样式、选择、图像与资源契约。见[默认模式实际观察](../artifacts/a5-display-mode-observation-draft-2/runtime-1-verification.json)。
