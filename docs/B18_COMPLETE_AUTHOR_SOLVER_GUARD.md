# B18 完整作者处方守卫

2026-10-12，status223。A–F 仍 active；本页记录隔离 source1070 / SDK31 / solver21 的精度回归修复，不表示 B18、A–F 或生产接入完成。G 继续暂缓，清理停止。

## 问题及修复

solver19 在所有端点关系作者都有明确唯一 FixPoint 时，提前保留固定坐标。但端点关系只覆盖区域来源的一部分；其他来源可能共享另一作者的活动生成端点。这类系统仍有真实耦合，不能仅凭端点关系完整固定，就把它当作只需求解生成端点的系统。

原 SDK31 的两配置 986 专项均为 863 通过 / 123 失败，其中 24 是原严格 STEP 导出拒绝，另外 99 是点坐标或材料体积超过原 1e-7 上限：PointCoupled 18、LivePointCoupled 72、PointCoupledParameter 9。同一测试和 SDK31 载荷仅换回已验 solver18 的 Sketching DLL，原 306 正例全部通过；实际加载的 62 原生文件仍为 SDK31。这定位到应用求解改动，不能据此归因 SDK31。

solver20 尝试在最终原生重放时同步生成坐标，但原 306 只通过 304，深层样条两个案例报 SKETCH.OUTPUT_PRECISION，且记录了不满足自然来源条件的请求；这版拒绝合入。其原 TRX、输出及来源保留。

solver21 改为收紧优化前提：每个未冻结区域的全部来源、每个未冻结表达式的直接来源，都必须是能够证明全部作者点明确唯一固定的原始曲线。活动生成端点、表达式来源、部分固定或无法证明的来源，保留原完整耦合求解。这是保守的适用范围，不新增曲线族支持。

只有 [两个来源的可读差异](../capability-evidence/AF_PENDING_SOLVER21_COMPLETE_AUTHOR_REVIEW.diff)：求解器版本与完整来源守卫。最终 Refresh、ValidateSolved、全部残差/分支、关联端点、完整 Jacobian、rank/DOF 保持；没有加入最终生成坐标同步，没有改变测试、1e-7 公差、256 变量 / 512 方程 / 80 迭代预算、作者身份或 STEP 导出守卫，也没有新增 OcctSharp 封装或修改公共上游。

## 实际验证

| 范围 | Release | Debug |
| --- | --- | --- |
| 完整 solution 构建及发现 | 0 警告 / 0 错误；原 6556 名称保持 | 0 警告 / 0 错误；原 6556 名称保持 |
| 原 986 功能专项 | 962 通过 / 24 失败 / 0 跳过 | 962 通过 / 24 失败 / 0 跳过 |
| 原 306 耦合正例，包含于 986 | 306/306 | 306/306 |
| 原 99 精度失败，包含于 306 | 99 全部通过 | 99 全部通过 |
| F5 路径引导及原上下文 29 | 29/29 | 29/29 |

以 [独立功能审计](../capability-evidence/AF_PENDING_SOLVER21_COMPLETE_AUTHOR_FUNCTIONAL.json) 为准。两配置逐名比较原 986：99 个原失败消除，剩余 24 个失败仍是原单环样条精确导出拒绝，没有新增失败；实际 SDK31 原生 62 路径与哈希绑定当前进程。Debug 使用私有包内 Release native，不能将其当成真正 Debug-native 验收。

本轮功能诊断使用独立进程、BelowNormal，在此前完整回归继续运行时执行；原等待期限保持，结果不用于性能、桌面或全量验收。原 306 保留所有坐标、完整方程、自然来源请求、两份材料、参数重算、撤销重做、JSON/MessagePack、寿命与资产断言；原冲突/关系拒绝及生成点相关案例仍包含在原 986 中。

[精确字节恢复补丁](../capability-evidence/AF_PENDING_SOLVER21_COMPLETE_AUTHOR.patch) 及 [恢复审计](../capability-evidence/AF_PENDING_SOLVER21_COMPLETE_AUTHOR_RECOVERY.json) 在 F5 source1070 恢复副本上实际应用，全部 1070 编译来源哈希一致。先恢复原 source1068、SDK31 依赖字节补丁、SDK31 清单和 F5 路径补丁，再应用 solver21；可读 diff 只用于审阅，字节恢复仍用 binary literal 补丁。

## 尚未完成及继续顺序

当前生产仍 source1000 / SDK26。solver21 两配置完整 6556 已排在原 source1068 两配置完整、旧 source1070 两配置相关/完整及原 Bonsai 两场景之后，没有停止或重启原活作业。上述旧载荷的未来结果不能替代 solver21 最终验收。

原 24 精确 STEP 导出拒绝以及独立原版 OCCT 的 6.9932867123069075e-7 mm 端盖误差继续保留。先完成这些严格交换问题及新候选完整门禁，再更新最终应用/窗口/材质/性能及提供方载荷；不直接合入仍有失败的候选。

A–F 的其余完整范围保持 [产品规划第 4、5 节](CAD_PRODUCT_GAPS.md#4-推荐怎么做下一步) 与 [实施跟踪](AF_IMPLEMENTATION_TRACKER.md)：A5 实际材质/上下文/GPU及性能、B 一般混合/C1实窗与交换、C/E 组合边界与生产接入、D 任意曲面/自由起始面/完整 Far、F 制造规则、外部元数据/几何等价及原 Bonsai。这个检查点没有实施新的 D 曲面能力，也没有执行清理、安装、签名或发布。
