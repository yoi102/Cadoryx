# C3 多副本样式与原生历史实施记录

更新：2026-10-04。属于 [A–F 全量请求](AF_IMPLEMENTATION_TRACKER.md) 的 G10/G17/G21，实施范围见 [续批契约](C3_MULTICOPY_STYLES_HISTORY_PLAN.md)。本页记录当前实现及验证，不将此子批视作整个 A–F 完成。G 第七批仍暂缓。

## 几何与原生来源

线性、圆周、任意帧镜像调用锁定 `.curve-inspection.10` 的 owned Transform/Compound；融合逐轮调用实际 Boolean history。完整 Face/Edge/Vertex 来源保留 `CopyId`、删除、分裂、共享及全部贡献路径。某一路曾经分裂或共享，即使后来回到一个结果也保持多解，不自动变成唯一身份。

结果保存为 BRep 后重新读取，比较完整有序拓扑（kind/orientation/parent）及每个槽的 canonical BRep。既有极小方向/pcurve roundoff 规则只处理已列举的解析方向重写；不根据面积、位置、最近距离或相似性重绑。完整图/历史仍有 100000 槽/条预算；超出预算明确报告未支持。显式 `TraceCopyAsync` 和 `inspect_copy_history` 要求作者指定实际 CopyId，成功后才返回经原生重验的精确选择。

## 样式与容器

默认保留已证明的 Gen/Surface/Curve 独立颜色、图层、物理/PBR 材料及可见性。来源 XDE 必须是简单定义，原始 BRep 和真实原生成员全图核对；只有根位姿非恒等时才正规化，避免无必要地烘焙 Compound 内部位姿。目标每个标签来自真实结果成员，不使用独立 repaired copy 冒充成员。

融合面/边/点的不同有效样式贡献拒绝提交。相同样式可以合并，但完整历史仍保留共享来源。明确选择“使用整体样式”才省略子形状分配，冲突报告仍保存；不复制源质心、验证属性、PMI、名称或任意文本属性。

Solid/Shell/Wire/Compound 等中间容器使用所有原生边界成员集合证明：每个来源边界和 CopyId 必须有唯一、未分裂/共享的原生路径，且存在唯一同 kind 的结果容器，其完整原生成员集合恰好相同。这只证明样式附着，不新增容器拓扑身份。容器分裂/合并、额外成员、多解或超过 2000000 次成员验证预算明确拒绝继承；作者可选择整体样式。

## 文档与生产入口

document45/features39/history4，capability `cadoryx.multicopy-presentation.1`。保存/重开重放整个重复操作，核对完整历史、来源/结果指纹、全部 report 字段和结果 XDE 的每个成员样式。无 verifier 的新证明文档拒绝保存；旧无证明文档不捏造证据。43→44→45 和 features37→38→39 使用固定中间格式，旧 schema 夹带新字段拒绝。

来源重算、数量调整、抑制/恢复保持样式与存活 CopyIds；独立化精确重映射新的 CopyIds，不重绑拓扑。三语言编辑器、Ribbon/特征树和 `cad_model` 共用策略。孔/材料特征重放不支持此整体策略，入口锁定默认策略，不能默默忽略作者选择。

工程图原始附着跨多副本停止；作者明确选择某副本后可建立新附着，并沿已证明的圆角历史继续。数量改变使旧后继与附着冻结，保留作者原资产。无效附着不能输出为有效 PDF。

## 最终验证

| 检查 | 结果与固定证据 |
|---|---|
| SDK `.10` 前提 | Release 与实际 Debug 各713；12元数据专项；205 Generator；16独立消费者；14不可变包/13托管/62原生 payload一致 |
| 应用专项/联合 | 191联合、18最终样式专项；最终52项包含B12迁移及FormatEvolution/SectionMigration，全部通过 |
| Release | `c3-styles-build-14.log`，整个方案0警告/0错误 |
| 完整回归 | `c3-styles-full-2/results.trx`，2296/2296，0失败/0跳过；首轮2294/2296的两项旧版本数字已修复并保留记录 |
| 生产窗口 | 同冻结`c3-styles-20261004-4`：windows4工程图及windows5重复/装配/C1/E1/桌面六门禁PASS；绑定日志为空 |
| 显示与PDF | 31重复及26工程图共57截图、4矢量PDF渲染页全部直接审阅；原生三视图另行核对，不以WPF截图证明native HWND |
| 交换复验 | `c3-exchange-sdk-7`、`c3-exchange-freecad-3`及`c3-exchange-presentation-3.json`绑定第四候选；24面/六色检查PASS，完整拓扑等价不算PASS |
| 哈希审计 | 17共享应用/2WPF/13SDK托管/62原生/14包绑定；`c3-styles-verification-1.json`保存最终TRX与先前冻结审计哈希 |

这是C3本地有界契约完成，后续B13使用隔离构建，不能把新源码或新DLL算作这份C3快照。整个A–F继续，G暂缓。

旧失败证据保留：元数据消费者首轮14/16（raw fingerprint 改写）、容器初版0/4（多余恒等正规化），第二版1/4（夹具对根槽0错误调用 subshape API）；第三候选工程图超时的根因仍未证明，不因独立重试通过而删除。

## 交换的实际边界

当前有限样本是四个分离盒体、24 个具有不同面色的平面。独立 SDK 的 Native XDE 与 STEP 同为137个完整拓扑槽，STEP 保留24个独立 Surface/Curve 通道和逐面图层；实际 FreeCAD 1.1.3/OCCT7.8.1 的 GUI `ShapeAppearance` 逐面核对六种颜色和装配/零件名称。FreeCAD 使用 sRGB，先对 native linear RGB 作明确转换，不能把 gamma 差异当作颜色丢失。

实测 STEP 读回丢失4个隐藏面标记、24个 PBR 分配和根物理材料；FreeCAD 没有逐面图层属性，回传 STEP 的逐面图层为零。FreeCAD 回传把首面颜色放在实体容器上，独立消费者通过真实原生成员关系读取继承颜色，不能只比较 direct face channel。

FreeCAD 回传还加入97个额外 Compound、96个额外 Edge 和384个额外 Vertex。24面色彩配对成功只证明本样本的可见表面属性，不是完整 BRep 等价或原始拓扑保持通过。这一回传差异继续属于 F4 互操作待办。GUI view-provider 在 offscreen 进程中取得，不证明外部 OpenGL 可见输出或物理指针。应用交换诊断和 capability/loss report 已明确将图层、逐面可见性及物理/PBR 材料与颜色分别验收。

最终对应证据：`artifacts/c3-styles-verification-1.json`、`c3-styles-full-2/results.trx`、`c3-styles-joint-5/results.trx`、`c3-styles-windows-{4,5}-summary.json`、`c3-exchange-presentation-3.json`。其余A–F工作继续。
