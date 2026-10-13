# GuidedSweep 与 AppSweep 缺陷的调用范围

2026-10-13。此次只读审查追踪当前 source1080 扫掠与实体螺纹入口，以及正在迁移的 SDK 源码。它缩小了已知 `GeomFill_SweepSectionGenerator` 变换缺陷的调查范围，**没有证明当前应用运行安全或完成算法修复**。[13 份来源及原文行号](../capability-evidence/diagnostics/appsweep-callers/verification.json)记录哈希、固定 OCCT commit 与准确限制。

| 入口 | 已核对的直接调用 |
|---|---|
| 单一直线作者扫掠 | 完整方向证明后用 `Extrude` 生成精确棱柱 |
| 曲线作者扫掠 | `GuidedSweepPlan.Create/Build`，`CorrectedFrenet` |
| 实体螺纹刀具 | `GuidedSweepPlan.Create/Build`，`Frenet` |
| 当前 SDK 资格源码 | `occtsharp_guided_sweep` → 生成器输入定义的 `LocalMakePipeShellBuilder` → `BRepOffsetAPI_MakePipeShell` |
| 固定 OCCT 构建源码 | `BRepFill_PipeShell::Build` → `BRepFill_Sweep::BuildShell/BuildWire` → `GeomFill_Sweep::Build` |
| 全局近似分支 | `GeomFill_SweepFunction` → `Approx_SweepApproximation` |

四份新收取的上游源码固定为 OCCT `b8f597c677811d1f9f4d8a97f5ae2825c0353a42`。`BRepFill_PipeShell.cxx`、`BRepFill_Sweep.cxx`、`GeomFill_Sweep.cxx` 中没有直接引用精确标识符 `GeomFill_SweepSectionGenerator`、`GeomFill_AppSweep` 或 `GeomFill_Pipe`。检查按完整标识符进行：首轮子串检查把状态枚举 `GeomFill_PipeError` 误判成 `GeomFill_Pipe`，修正匹配方式后完成记录；没有改变任何生产代码或行为断言。

作为对照，固定源码的 `GeomFill_Pipe::ApproxSurf` 明确创建 `GeomFill_SweepSectionGenerator` 并交给 `GeomFill_AppSweep`。因此该缺陷有实际上游调用者，不能把 AppSurf 与 AppSweep 当作等价算法，也不能笼统说所有 Pipe 或所有扫掠都受影响。

[既有归档缺陷证据](../capability-evidence/diagnostics/appsweep-upstream/verification.json)仍保留：普通切向分支漏更新 `TR=Rot*Trans`，21 截面直线路径末 Z 为 0、期望 3；探针退出 0 表示复现缺陷。当前 76 程序集元数据没有直接消费这些低层类，但元数据不能证明原生内部传递调用。

此次没有穷尽辅助类、虚函数和完整传递调用闭包；正在迁移的 SDK 源码也不是公开 Preview.85 实际加载 DLL 的来源证明。最终载荷的扫掠端截面、螺纹形体、后继历史和完整运行回归仍待执行，保留原公差与断言。没有据此提出新公共 API、修改生成器输出或启动原生程序。必要的新 API 将继续交给现有 OcctSharp 会话优先生成，避免制造重复包装。

上游会话最新报告冷源码全量 2253 通过、2 项窗口输入失败（CC 选择重绘与 CB HWND 选择计数）；这里只记录上游报告，尚未独立接收终态资格。精确本地 Preview.86 包与消费者仍待交付，Cadoryx 保持 source1080/公开85。完整 A–F、原 STEP24、最终模型/性能/窗口门禁继续，G 与清理暂缓。
