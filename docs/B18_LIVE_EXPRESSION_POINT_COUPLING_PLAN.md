# B18：实时表达式链提供的生成点耦合

2026-10-11。A–F 继续，G 暂缓。本批补齐现有直接生成点耦合之后的实时表达式链。隔离修正版已通过 Release/Debug 构建（各0警告/错误），两配置清单均为原6122+新270=6392，各270专项全部独立通过；完整6392和C1实窗尚未运行，本子项专项通过不代表整个 B18 完成。

现有 `TryCreateForEquations` 只接受直接区域输出点的跨组依赖；中间实时表达式仍被拒绝。这条保护保持原样。新入口先复用已有独立和直接点证明，再尝试记录消费组、源曲线、生成点、实际点提供者、完整实时表达式 ID 链和区域提供者。冻结表达式仍截断依赖；来源循环、深度超限、未证明路径及直接几何消费继续拒绝。

为每个迭代候选，从当前独立坐标推导已证明自然组的完整平行曲线，再按原依赖顺序刷新中间表达式和原生组。该候选只能用于方程求值，不保存或发布：不覆盖生成点坐标、不修改作者 ID、接头或持久化区域表。生成点仍为变量，全部结构方程及解析导数保留。提交仍执行完整原生刷新、`ValidateSolved`、来源/接头对应和端点一致性复验。

| 待验收矩阵 | 计划场景 | 必须观察的结果 |
| --- | ---: | --- |
| 三曲线家族 × 两依赖方向 × 1/3/7 层 × 镜像 × 正负距离 × 三接头 | 216 | 实际求解、所有变量/方程、完整来源与端点 ID、无离开自然接头流形的原生请求、两个实体的独立体积、一笔撤销重做、生产文件及 JSON2/MessagePack3 往返、最终零资产 |
| 三曲线家族 × 两依赖方向 × 三深度 × 两尺度 | 36 | 非零原点和旋转后的全部源及生成点列；独立中央差分核对完整残差，原相对界限 3e-5 保持 |
| 三曲线家族 × 两依赖方向 × 三深度 | 18 | 显式冻结中间提供者后，依赖链截断，既有证明入口保持原行为 |

原 256 变量、512 方程、80 次迭代、深度 64、体积 1e-7 和全部旧断言保持。这里的 ID 链证明局部作者依赖，不证明导入、拆分/合并或跨操作原生拓扑身份，也不宣称全局唯一解。

目前八份 C# 草稿只通过 Roslyn 语法检查。第一轮查询变量误用了 `join` 关键字，失败保留；修正版只改变量名。准备阶段也纠正了新局部变量重名和测试中对强类型 ID 的排序方式，旧测试与生产源码未改。

- [当前隔离源码与 270 场景准备](../artifacts/b18-live-point-equations-draft-1/preparation-4.json)
- [八文件语法核对](../artifacts/b18-live-point-equations-draft-1/syntax-2.json)
- [首轮语法失败](../artifacts/b18-live-point-equations-draft-1/syntax-1.json)
- [已验 SDK27 统一候选两配置构建与各 802 相关](../artifacts/a5-sdk27-control-unified-draft-1/build-related-both-1-verification.json)
- [原直接点耦合边界](B18_COUPLED_POINT_GROUPS_NEXT_PLAN.md)
- [A–F 全部缺口](AF_IMPLEMENTATION_TRACKER.md)

先完成当前 SDK27 统一候选的完整两配置 6122、材质/窗口和原性能门禁，再串行编译、发现并执行本批。原 6122 的源码、断言和预算必须完整保留；270 是待发现数量，不能提前记为通过。之后补 C1 参数预览/确认/取消和三语言生产窗口。直接表达式几何消费、同环混合接头、其他关系诊断及全过程几何/原生历史仍继续。

2026-10-11 status181：[首轮语义编译失败](../artifacts/b18-live-point-equations-draft-1/build-focused-both-1-exit.json)是测试跨程序集访问internal AuthorPoints；[workspace2修正](../artifacts/b18-live-point-equations-draft-1/workspace-preparation-2.json)只使用反射访问既有方法，未公开/修改生产API，所有测试断言、270矩阵、容差与预算保持。原workspace1和source4保留。当前两配置构建已通过，专项仍在原串行流程中运行，不从单项日志推断完成。

2026-10-11 status182：[两配置专项独立核验](../artifacts/b18-live-point-equations-draft-1/build-focused-both-2-verification.json)通过全部216实际求解/双实体/存储、36全Jacobian和18冻结截断；实际SDK13/原生62及原6122名称/源码均绑定。Release3.2602分钟、Debug3.5316分钟。首次编译访问internal方法失败保留。下一步按[完整6392准备](../artifacts/b18-live-point-equations-draft-1/full6392-preparation-1.json)执行原清单全量两配置，再做C1参数预览/确认/取消与生产窗口；不改原预算或拓扑边界。

2026-10-11 status183：见[当前合并验收](AF_MERGED_LIVEPOINT_DRAWING_DENSITY_PLAN.md)。两配置6422发现及各350联合通过，完整6422运行中；C1实际窗口未运行，Bonsai工程图/多文档最新两项FAIL已独立保留。SDK28原生健康状态8文件/12测试为未编译草稿，WPF自动恢复尚未完成。生产1000/SDK26和正式子批进度保持，G暂缓。
