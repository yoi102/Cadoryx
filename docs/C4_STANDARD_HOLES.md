# C4：孔特征、常用尺寸与编辑流程

更新：2026-10-01。属于 [A–F 实施](AF_IMPLEMENTATION_TRACKER.md) 的 C/G11 常用孔型范围；整个前六大批仍在进行，第七批暂缓。

## 已实现契约

- 通孔、平底盲孔、沉孔和沉头，毫米尺寸；沉头夹角 60°–150°。盲孔总深度从入口平面量起，头部深度必须小于总深度。
- 入口由零件局部刚性帧及 U/V 确定，沿该帧 Z 负方向钻削。已建 Box 默认使用其设计顶面和中心，不从任意实体的包围盒猜测入口面。
- 只消费同零件中显示的精确当前源特征输出，保留生产特征、层/材料/颜色等属性；目标和结果必须各为一个有效实体。锁层、外部来源、失效源和取消原子拒绝。
- 三语言生产窗口支持新建、编辑、预览、确认、取消、特征树入口，尺寸改变立即使预览失效。确认只有一条历史记录；关闭取消预览并释放资源。
- Common metric clearance v1：M3/4/5/6/8/10/12/16，紧/普通/松间隙，标准规格和实际孔径独立保存。标准绑定时不允许自定义孔径；明确解除后保留当前尺寸。此表是明确列举的常用子集。
- 参数字段：Diameter、U、V；盲孔 Depth；沉孔 HeadDiameter/HeadDepth；沉头 HeadDiameter/HeadAngle。HeadAngle 是角度，其余是长度。多个尺寸全部暂存后验证最终配方，不因合法修改过程中的中间值而失败。
- 关联 C2 同零件基准面；源尺寸、基准来源、基准数值、参数和 Boolean/阵列后继按联合依赖图原子重算。基准帧先于孔原生重算刷新，避免旧入口平面被错误使用。关联帧修改需明确解除。

## 精确几何与拓扑边界

普通孔使用锁定 `.cadoryx.modeling.mirror.2` 的受控 `LocalFeatures.Hole`，原生盲孔失败状态不能被当作贯穿成功。沉孔先钻头部盲孔，再钻主体；沉头先以受控盲孔验证轴向剩余材料，再切精确圆锥，最后钻主体。

反向正例核查发现：原生 ThroughAll、UntilEnd、BetweenBounds 在此边界样本均可能切到入口后方。应用额外构造指定方向的有限圆柱，与精确目标 BRep 做 Common，比较实际去除量；没有切削或多切入口后方材料一律拒绝。包围盒只用于保证通孔校验刀具完全覆盖目标的长度，不推断盲孔封底或拓扑对应。

普通孔的单阶段历史沿用原生证据及 BRep 读回逐槽验证。沉孔/沉头包含两阶段，现阶段输出明确 `HISTORY.UNSUPPORTED`，不把头部或主体的单阶段索引伪装成原始源到最终结果的历史。最终边/面可重新精确选择；分裂、合并和歧义继续拒绝猜测。

## 文件

当前 document v23、features v24，仍为十五节；必需能力 `cadoryx.standard-holes.1`。配方追加 Key(14) PackHole，保存全部孔尺寸、入口帧、标准尺寸表名称/版本、规格/配合和基准身份/修订。features v23→v24 拒绝旧版夹带孔配方或孔配置；document v22→v23 明确迁移，不跳过已有 C1/C2/C3 规则。

## 当前证据

- 新孔专项 20/20：`artifacts/c4-tests/c4-hardened.trx`，含六种模式体积、旋转方向、盲孔过深/反向/入口后方拒绝、无资产泄漏、参数/基准/源/阵列后继、两种绑定顺序、旧文件及伪装拒绝、STEP 精确读回及三格式导出、抑制/恢复。
- 联合孔/参数/基准/重复特征 54/54：`artifacts/c4-tests/c4-joint-hardened.trx`（随后孔专项增加五项）。
- 独立锁定 NuGet 消费 37/37，62 DLL 全部匹配：`artifacts/occt-capability-20261001-190201`。保留此前方向反例失败证据，没有改成“原生拒绝了反向”这一错误结论。
- 首轮全量 852/852、Release 零警告/错误、桌面 PASS；孔窗口首轮按钮没有收到点击事件，失败保留在 `artifacts/c4-ui-verification.log`。窗口现受工作区高度约束、内容可滚动。后续诊断证实 Windows 实际命中 Chrome：按钮启用、窗口可见且未被 DWM 隐藏，但 `WindowFromPoint`/前台 PID 属于 Chrome，WPF 没有收到 Click。此项保留为桌面指针门禁未通过。
- 完整指针回归 `artifacts/c4-final-verification.log`：全量 857/857，Release 零警告/错误、桌面及 B1–B7 PASS，停在 C1 实际点击“应用参数行”；C2/C3/C4/工程图未因此报告 PASS。
- C4 单独 WPF 自动化 Invoke 路径 PASS：`artifacts/c4-automation-invoke`，en-US/zh-CN/ja-JP 的生产控件、预览体积、确认/关闭/取消、特征树、撤销/保存重开和零资产；绑定日志为空，三语言截图检查。按钮实际触发生产 Click/Command，但不证明桌面指针命中。输入方式后续写入结果文本。
- 最终全量 **857/857**、Release 零警告/错误、桌面及 C1/C2/C3/C4/工程图/PDF PASS：`artifacts/c4-automation-final-verification.log`，脚本退出 0，发布 `artifacts/publish/20261001-193010`。六份绑定日志为空，各窗口关闭后资产归零。C1–C4 明确使用 WPF Automation Invoke/Toggle，结果文本标记桌面指针命中 NOT VERIFIED；默认门禁仍使用注入鼠标，没有失败后的自动替代。

复现：`./scripts/verify.ps1 -C4HoleSmoke -C3RepetitionSmoke -C2DatumSmoke -C1ParameterSmoke -DrawingSmoke -AutomationButtonInput`。原生/单元与自动化控件范围已完成；A 批桌面输入环境及物理设备证据继续独立跟踪。

## 仍保留的范围

符号螺纹/孔标注与真实螺旋实体是分别验收的产品范围，普通间隙孔不代表螺纹已实现。C3 参数特征重放/装配实例、C2 基准轴/点及任意精确面来源、D/E/F 和 A 性能/交互剩余项继续由总跟踪表管理。

D1 锁定消费已启动：正反/双向拉伸与明确平面终止有精确体积、上下界证据，联合 probe 38/38、62 DLL 匹配，`artifacts/occt-capability-20261001-191448`。后续应用实现见 [D 契约](D1_ENTITY_EXTENSION_PLAN.md)。
