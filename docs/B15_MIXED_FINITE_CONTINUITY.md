# B15 混合有限参数 G1/G2 实施记录

2026-10-05。第二候选完成本文本地几何、应用、窗口、消费者、视觉及冻结载荷检查；最终冻结完整回归2563/2563 PASS，37m14s、0失败/0跳过、session4440退出0，B15本文本地契约已验收。整个B及A–F仍继续，G交付暂缓。契约见[B15计划](B15_MIXED_FINITE_CONTINUITY_PLAN.md)。

同一个有限参数接触作者增加明确 G1/G2 阶数。支持线段、三点圆弧、圆、二次 Bézier、夹持非有理三次作者样条、固定精确椭圆及已有复制的有理 Bézier/样条。两条曲线、两个内部参数及同向/反向都保留稳定作者身份；不移动到另一交点，不合并点 ID。圆从局部 +X 逆时针参数化；圆弧中点选定方向；复制曲线保留原有限域及反向，求解时保持固定。

G1 为两个位置方程和一个单位切线方程，G2 另比较有符号曲率；反向关系比较相反曲率符号。方程分别计三/四行，保留 256 变量/512 方程及既有毫米、角度、曲率公差。生产 Jacobian 以解析链式微分传递作者点/圆半径的梯度，圆弧包含圆心、半径、起始角和有向扫角的导数，跨 atan2 表示缝只加常数。差分只用于审计，不用于生产求解。

复制有理曲线增加精确齐次二阶导数及商法则，先减原点以减少平移消减误差。复制样条在结点和周期缝必须证明所需导数阶数，G1 可接受 C1、G2 需要 C2；不明确的侧导数拒绝。原作者三次样条的重复内部结点明确拒绝契约保持。退化速度、圆弧方向反转、错误切线分支、错误阶数和不能收敛的几何不发布候选；局部失败不报告全局无解或唯一。

三语言生产编辑器可创建/修改明确阶数，输入变化立即废弃预览；撤销/重做恢复阶数。strict `cad_edit set_sketch_finite_contact` 接受可选 `continuity=G1|G2`：新建省略为 G1，编辑省略保留旧阶数，不默默降级 G2；检查输出包含阶数及曲率残差。参数驱动保留阶数并原子重算 Bézier 实体和关联图纸；独立化保留本地曲线/约束身份及自己的驱动，修剪/分裂/延伸清理旧接触而不重解释参数。

document51 / sketches20，features42 / drawings14 / history4 不变；solver `cadoryx.sketch.lm-svd.10`，能力 `cadoryx.sketch-mixed-finite-continuity.1`。G2 保存为独立 typed kind `finite-parametric-curvature`，不会重开为 G1。document50→51 与 sketches19→20 独立迁移：真实旧 G1 作者保留，旧格式夹带 G2 或新混合曲线目标拒绝。第一候选三语言各1313键；第二候选增加明确的参数/阶数拒绝消息，三语言各1314个非空资源键。变更同时覆盖应用按钮和隐式预览的错误翻译，保持原参数守卫及原子拒绝。

| 验证 | 当前证据 |
|---|---|
| 锁定SDK独立导数前提 | 64有向样本PASS，62隔离原生DLL；`artifacts/b15-native-3/result.json` |
| Release构建 | build6，0警告/0错误；`artifacts/b15-build-6.log` |
| 最终联合 | 同final-build-1的311/311 PASS，11m39s；包含65项B15、36曲面、55螺纹及7原生投影编辑；`artifacts/b15-focused-4/results.trx` |
| 第二候选窗口 | 八组PASS，空绑定日志，三语言十二个新作者场景及既有功能回归；`artifacts/b15-windows-2-summary.json` |
| 同冻结独立消费者 | 十二窗口保存文件的原生点/切线/曲率、体积、驱动/Top/Front/PDF、非法输入/撤销重做/重开/资产归零PASS；`artifacts/b15-consumer-evidence-2/result.json` |
| 视觉 | 第二候选24截图/3矢量PDF全部直接审阅并复核原始SHA；中/日拒绝提示及阶数按钮完整可读；`artifacts/b15-review-2/review.json` |
| 冻结载荷 | 17共享应用/2WPF/13SDK托管/62原生/14不可变包一致；八消费者应用DLL、三语言1314键及两卫星资源DLL一致；`artifacts/b15-verification-1.json` |
| 最终完整回归 | 2563/2563 PASS、37m14s、退出0；`artifacts/b15-full-1/results.trx`及`artifacts/b15-final-regression-1.json`绑定原final-build-1测试DLL SHA，5分钟hang预算不变；不替代B16新源码验收 |

此前失败保留：原 SDK 探针错取 `SketchEvaluation.SecondDerivative` 编译失败，二阶导数由已有真实原生 edge API 提供；探针路径前缀误把四个 `OcctSharp*.dll` 托管程序集算作原生目录，第二轮保留实际模块清单，精确目录比较后仍要求62原生DLL并通过。应用 build1 有变量同名编译错误、build3 的新集成测试缺通知命名空间，均修复后 build5 零警告/错误；未把旧测试 DLL 结果冒充修复后新测试通过。消费者首轮误写几何引用类型编译失败，修正为 GeometryAssetRef 后独立构建并运行通过。

当前冻结最终全量已经收取，历史结果继续保留。第一候选65专项/213联合、十二窗口/消费者及七组既有窗口均通过，但8张中/日limits图的英文错误提示保留为问题；第二候选已修复并重验全部本地门禁。focused3不支持`/BlameHang`而未运行测试的CLI错误保留，focused4使用受支持的五分钟hang参数。接续[B16参数自动求解](B16_SLIDING_CONTACT_PARAMETERS_PLAN.md)，其导数/区间基础192项已验，自动作者/求解及完整验收继续；偏移连接、A性能/真实输入、D更多曲面/起始面/新实体、E曲面编辑/缺失历史、F制造规则/外部元数据/供应商失败仍继续。
