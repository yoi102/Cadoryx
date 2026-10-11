# F3h：双面尺寸、中面基准与双作者延续

更新：2026-10-03。接续[F3e圆柱尺寸](F3E_TYPED_DATUM_SYSTEMS.md)、[F3f作者附着](F3F_ORIGINAL_ATTACHMENT_HISTORY.md)及[F3g组合钻孔](F3G_COMPOSED_DRILLING_HISTORY.md)。本页明确的有界应用契约已实施并验证。整个A–F继续，G实机、安装及版本交付暂缓。

## 原始完整有限域证明

外部厚度要求一个完整、有效、正体积的直线多边形棱柱及明确选择的两个cap面。原始邻接逐面、逐边证明每个侧面连接两cap、生成线与厚度方向一致且边界完整；三角形、矩形和凹多边形包括旋转及STEP来源已验证。不能仅凭两面的Plane分类、包围盒或设计参数推断板厚。

内部宽度要求该已证明棱柱中的整个矩形贯通空槽，支持开口U槽和闭合矩形孔槽。两处明确原始矩形壁的有限域必须精确平移重合；所有cap直线边不得进入整个开槽矩形内部，随后解析even-odd分类证明该连通域为空。第二面由用户选择，不能通过距离寻找。

原生SurfaceOfExtrusion侧面仅在精确ConstantV基曲线和ConstantU生成线均为Line时证明整面平面性；不是抽样法向推断。完整图最多4096条目、5–66面、256边，每cap为3–64条直线边；不能将此契约推广为任意平行面或任意曲面尺寸。

NativePlanarSizeInspector已由工具移到核心，独立消费者链接同一源文件。锁定curve-inspection.6的55场景/62原生DLL审计通过；29前提包括20正例及9正确拒绝，覆盖作者/STEP、对齐/旋转、外部多边形/内部槽、同面/非平行/材料侧/域不重合/多实体/曲面拒绝。原始来源不变；原生前提的scope保留当时“应用待接入”的原文，不能混作完整应用证据。

## 制造类型与双作者附着

DrawingPlanarSizeEvidence保存Source、OppositeSource、方向、宽度、中面原点、内外类型和完整域/邻接见证。制造基准新增PlanarSizeMedianPlane，公差目标新增MedianPlane；与普通表面及完整圆柱轴分列。中面支持当前Flatness、Parallelism、Perpendicularity、Angularity及Position规则，非直径容差域；材料条件消费完整证明。共同中面要求共面，不混合不同制造类型。自引用及重复基准检查两面。

来源重选对两面分别沿唯一可证明原生历史重放，到达同一当前实体后重新验证整个有限域；任何一面失败都保留两处旧作者/证明并标失效。转角Boolean样本使A外部厚度4 mm的两个作者面分别延续，并从新BRep重验当前proof；另一个未改变的零件保持B内部槽宽6 mm及原始双面证明。该窗口的bothAuthorsContinued指A的两面，不指A和B均经Boolean。显式重选任一基准面仍使依赖控制框失效，需要明确确认；模板无变化保持完整记录及来源。

## 存储与生产入口

drawings独立升级v12，新增必需能力cadoryx.planar-manufacturing-sizes.1。Datum追加数字键8、Frame键14、Binding键3，平面证据DTO独立键0–11。v11→12保留原有圆柱/孔/作者历史，拒绝旧版本夹带新字段，包括nil、空数组及新枚举2；旧10→11固定生成v11。document39/features35/sketches16/datums2、history3及SDK不变。

有效datum/frame保存与加载逐项重验两面及整个proof，缺解析器拒绝。第二面作者资产与失效旧来源持有租约；失败保存不替换原文件。实际制造基准绑定由其有效datum核验，datum可属于另一个视图，不错误地按控制框视图重验第二面。

工程图编辑器增加第二原始尺寸面选择、外部厚度/内部槽宽及中面说明。验证期间Add/Apply禁用，取消/替换保持可用；selection generation、窗口lifetime、会话StateId及snapshot lease守卫晚到结果。视图改变、孔选择、关闭/取消/脱离或状态变化使旧前提无效。WPF/PDF共用中面Origin引出线及矢量控制框，中/英/日资源完整。

## 当前验收记录

最新28/28针对性检查通过，包含25个新用例及3个旧匹配项；较早build4联合288/288通过，之后补充四个晚到守卫及修正，最终全量1683/1683覆盖全部25新用例。build5全解Release零警告/错误。固定发布artifacts/publish/f3h-planar-20261003-1的八窗口通过：F3h、F3g、F3f、F3e、F3c、图纸、桌面及E1游标，bindings/stderr均空；17共同托管DLL与最终测试一致，62原生DLL与55场景SDK审计一致。最终汇总artifacts/f3h-planar-verification-1.json；核对完成后该发布及证据冻结，后续F4新增代码不重写这组验收。

三语言6新控件截图/3新PDF及同发布27旧控件截图/12旧PDF共48图已逐张检查；所有15PDF为单页A4横向矢量内容、比例2:1、零嵌入光栅。截图长内容使用滚动面板，不意味着所有字段同屏可见；忙碌时变暗的真实捕获保留。逐图哈希见artifacts/f3h-planar-visual-review-1.json。

SDK2/3早期extrusion表面拒绝、core build2缺失UnitX和diff1末尾空行均保留。自动化按钮和原生两面选择API不是实体指针、实纸或外部CAD验收，完整制造标准合规亦另列。A固定大模型性能目标、B/C/D/E余项及进一步制造规则继续；下一项为[F4交换实测与往返](F4_MEASURED_EXCHANGE_PLAN.md)，命令/AI仍在[总跟踪](AF_IMPLEMENTATION_TRACKER.md)。
