# B18 精确 STEP 偏移曲线修复

2026-10-12，status212。A–F 继续，G 暂缓；清理已停止。生产仍 source1000 / SDK26，应用候选仍 source1068 / SDK30，原65项仍41通过/24导出失败。以下区分新的SDK31草案2与冻结草案1；未打包、未接入应用，不报告B18或A–F完成。

## 草案2：单位、精确参数曲线与实际入口

新的生成器模板按照实际STEP表示上下文计算长度单位，写入要求与已完成传输的因子一致；读取只在同步传输期间缩放偏移距离，并在正常/异常退出时精确恢复STEP模型。10种写入单位、毫米/英寸/米三个系统单位已有实际公开入口证据。合法多单位上下文共用同一实体的克隆仍未实现；冲突因子明确拒绝，不猜单位。

严格公开API首先发现 `spline-g1-mirrored-inward-Bevel` 无效，共享样条的第二个平移实例也失败。实际三维边与平面端盖近似pcurve相差约6.9933e-7 mm，不能作为测距误判消除，也没有提高公差。新生成支持根据实际edge–face成员关系，将直线/圆/椭圆、Bézier和样条的全部控制点/轴精确转换到平面坐标，保留原度数、结点、重数、权重、周期、参数范围和offset；只允许证明共面的基曲线与法向偏移。反射/间接平面保留两个坐标轴与正确偏移符号，没有采样拟合或近似曲线替代。它只修改新导入的本地BRep；会话结果另有深复制所有权，重复传输不会修改先前返回的形状。

Debug写出随后发现部分offset实体的传输绑定为空。生成器在深几何副本中临时去除offset，建立受支持的STEP实体骨架，再使用 `BRepBuilderAPI_Copy.ModifiedShape` 的实际复制映射将原始精确几何补回；仍要求每个STEP绑定唯一，临时骨架不写出或返回。原始形状不变，非offset写出保持原入口。实际C ABI适配器已编译接到普通/报告/会话以及XDE/样式/merge读取路径；XDE公开写入端到端尚未验收。

- 两配置生成器各248/248，零失败/跳过；标准入口186输出两轮一致，原182份字节不变，26040声明ID保持。
- 实际Release/Debug原生与托管完整构建通过；托管均0警告/0错误，41174导出名称及顺序保持。
- 两配置公开矩阵各1182通过：原36写出后普通/报告读取72，原420单位文件两入口840，90会话重复/释放后owned结果270；严格 `GetValidationReport(true,true)`、原1e-7体积/逐面面积与源BRep字节不变保持，实际native62路径/哈希均已核对。这是公开API诊断，仍不是包消费者。
- 两配置精确转换各160正例及6拒绝/原子性反例通过，最大坐标误差1.85e-14；新增4项真实公开写出/读取/会话/所有权回归。相关各138、完整各1386均通过，零失败/跳过，原测试源保持。
- 长路径的Release5/Debug1原IO失败及首轮短路径漏复制同样5/1失败保留；修正复制工具使用扩展路径，每配置212文件逐一同哈希复核后，原夹具/断言不变通过。没有清理。

**外部交换尚未完成。** 独立原版OCCT读取最新出站样条文件、未调用新pcurve修复时，严格检查仍失败，端盖误差仍6.9932867123069075e-7 mm。[原始诊断](../../OcctSharp/OcctSharp/artifacts/cadoryx-exact-offset-step-generated-31-draft-2/pcurve-outgoing-stock-reader-1.log)保留；本库读取成功不能替代该条件。XDE仅私有几何副本/关闭形状处理的诊断在Release18/18通过（额外严格BRep检查、计数和源不变）；Debug18项仍失败，默认绑定失败也保留，未证明完整PMI/层/材质/共享文档隔离或质量/边界等价。

[独立核验](../capability-evidence/AF_PENDING_SDK31_UNITS_PCURVES_VERIFICATION.json)绑定本轮来源、构建、实际载荷与完整原矩阵。[第三版恢复补丁](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V3.patch)和[元数据](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V3.json)保存相对已验SDK30的34个文件，在独立副本应用后全部逐字节一致；binary literal保留换行/夹具字节。补丁包含生成器模板/契约、标准生成产物、实际适配器和新增回归，不含编译缓存；公共上游工作流未修改。

下一步先完成出站参数曲线图处理及独立重读，XDE完整文档副本/共享结构/PMI隔离、单位缺失/冲突/溢出反例及更广周期内部极值，再取得SDK31身份、私有包和干净消费者。之后接入source1068原65/相关986/完整6532及A–F剩余窗口、材质、性能、原Bonsai、一般混合和制造交换要求。原 `EXPORT.EXPRESSION_UNREPRESENTABLE`、unsupported-freeform拒绝、已有目标文件/作者树/资产不变条件没有放开。

## 草案1已验证记录（status211）

OCCT 8.0.1 的 `GeomToStep_MakeCurve` 没有 `Geom_OffsetCurve` 分支；读取器的 `StepToGeom::MakeCurve` 已支持 `StepGeom_OffsetCurve3d`，但距离取负，且该分支没有乘长度单位因子。官方源码引用固定在 `b8f597c677811d1f9f4d8a97f5ae2825c0353a42`；原实际SDK30探针的两个失败文件各有4个空扫掠基曲线引用。

新支持从 `ExchangeSupportWorkflowEmitter`、`step-exact-offset-*` 模板和配置生成，未手改生成产物。通过实际源形状到STEP实体的唯一传输绑定补写 `OFFSET_CURVE_3D` 及拉伸面基曲线；缺失、多重绑定或未闭合图引用直接拒绝，不按索引、距离或法向猜测对应关系。偏移距离按已验证的读取方向处理；自相交标志保留未知。目前只验证毫米，其他单位仍明确拒绝，后续必须补读写两端单位处理。

- 最新 Release、Debug 生成器完整各248/248：原230、STEP新增9、测距新增9均通过，零失败/跳过；原测试源不变。
- 标准生成入口两轮一致，产出186份文件；原182份逐字节保持，26040个声明ID保持，仅新增4份内部原生支持文件。
- 完整SDK原生桥及托管依赖已分别完成Release/Debug构建，原生最终零警告/错误；原41174个导出名称和顺序保持，没有新C ABI或公共托管类型。包和应用接入仍未完成。
- 原36个B18案例通过未修改的source1068程序集和实际SDK30/native62采集真实BRep；每例原求解2次迭代、资产回收保持。
- 原生探针实际观察32份所用OCCT DLL路径及哈希。36份STEP均真实写出/重读，每份8条偏移边、4个偏移拉伸面、12个偏移实体的图引用完整；没有空扫掠基曲线或悬空编号。
- 毫米以外单位、多重传输绑定、未解析扫掠引用3项实际拒绝通过；拒绝前后原始BRep序列化逐字节一致。

权威记录：[最新独立核验](../../OcctSharp/OcctSharp/artifacts/cadoryx-exact-offset-step-generated-31-draft-1/generator-native-probe-2-verification.json)、[原36源捕获](../artifacts/b18-exact-step-brep-source-draft-1/capture-1-exit.json)、[最新原生终态](../../OcctSharp/OcctSharp/artifacts/cadoryx-exact-offset-step-generated-31-draft-1/probe-runtime-4-exit.json)。

## 原始失败与测量误判复核

旧原生探针保留原1e-7体积、面积和双向边界采样条件，原始11通过/25失败及文件均未改写。该探针直接调用普通BRepGProp和BRepExtrema，不是产品的高精度质量入口。36例的源/重读BRep均有效且各有一个实体；这些检查不能代替应用导出验收。

普通质量积分的往返体积差最大0.0102429288373 mm³、面积差最大0.0204858575767 mm²。原生自适应积分往返差虽约1e-9，其源体积仍与独立Green积分相差最大0.359123355295 mm³。独立三维边/面参数曲线积分沿真实样条结点分段，保持1e-11精度与20层预算，原36例均符合原作者预期。进一步复用未修改的产品 `InspectionIntegration.cpp`，体积使用原应用的1e-11相对条件，面积按公开契约逐面计算：36例均成功，源体积相对作者最大差1.14e-13 mm³，重读最大差2.23e-9 mm³，往返面积最大差4.35e-9 mm²。产品已有的原始边界/结点积分正确，不需要用普通GProps替换它。首个内部消费者对整个实体直接请求面积造成10个分割错误，保留记录；改为逐面调用后全部36通过，没有修改产品积分实现或断言。

三个普通向外Round的反向边界旧采样最大偏差0.0765366864730 mm。真实源边17/18的参数为[-2.83159265358979,-1.2607963267949001]；源曲线中点对源模型自身也被BRepExtrema误测为0.076536686473016 mm，原区间直接投影无结果。按曲线真实周期等价平移2π后距离为零；重读边的正周期区间自身投影也为零。这是负周期投影误判，不能据此重建、拟合或改写STEP几何。

## 已生成并接入的实际测距修复

新增独立 `InspectionSupportWorkflowEmitter`、`inspection-periodic-*` 模板及配置，生成内部 `OcctSharp_PeriodicExactDistance`。现有手工C ABI适配器调用该生成实现，覆盖普通距离、完整见证距离、形状对分类和数字样机分析。仅在真实周期曲线的区间超出原基本周期时补充顶点到边投影；跨周期投影按真实接缝分段，映回原始作者区间。源形状、范围和pcurve不变，见证点、支持形状和参数一起返回，计数/结果一致，原所有权保持。内部边到边、边到面、面到面的极值仍由OCCT原算法处理；尚未实现其负周期内部极值补充，不能称所有周期测距边界完成。

- 240个圆/椭圆解析案例通过：负、正、跨接缝、多周期区间，曲线上点及离面点，两个操作数顺序、重复计数、见证参数和源BRep不变。
- 原36例双向边界及源/重读自距离全部满足原1e-7。双向最大差分别4.49e-12和5.08e-12 mm，源自距离最大5.35e-14 mm；未改旧探针或其失败记录。
- 实际公开API新增12例全部通过，保留原负参数、支持形状及输入释放后的见证支持寿命。同一托管载荷换回冻结SDK30桥，12例全部复现失败；该快速对照没有捕获运行模块，仅核验其磁盘载荷，不冒充模块观察。
- Release/实际Debug原生的扩展专项各134/134：原122及新增12保持，两组均实际观察62份原生DLL路径与哈希，61份依赖与冻结SDK30对应配置一致。首轮长路径5个原BRep读取失败保留，完整载荷逐字节移到短路径后原断言全部通过，没有改测试路径规则或公差。

权威记录：[专项与独立36核验](../../OcctSharp/OcctSharp/artifacts/cadoryx-exact-offset-step-generated-31-draft-1/periodic-distance-1-verification.json)、[负周期原始诊断](../../OcctSharp/OcctSharp/artifacts/cadoryx-exact-offset-step-generated-31-draft-1/boundary-inspection-1.log)。完整1382及实际包消费者仍NOT RUN。

初版编译/生成/积分诊断失败保留。没有放开应用 `EXPORT.EXPRESSION_UNREPRESENTABLE`，没有修改现有freeform拒绝、原B18断言、精度或求解预算；非毫米偏移单位、实际shape/XDE写入桥接、SDK包、原应用65/相关986/完整6532、最终窗口/性能/材质、原Bonsai和生产合入仍须分别完成。部分来源驱动/一般混合求解、F4完整交换/PMI等剩余范围继续按产品规划第4、5节推进。

## 恢复与接入

[SDK31第二版恢复补丁](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V2.patch)和[元数据](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V2.json)保存相对冻结SDK30的8个修改及18个新增来源/输出/夹具。原[第一版补丁](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP.patch)保留。新补丁SHA256为 `1F849F9B0D5128721605A406E2B233791A04D57948206F61F2FED62A65D51FE2`。已在冻结来源上仅检查，并在独立副本实际重放，全部26文件与已验候选逐字节一致；公共上游和冻结来源未应用。初版文本重放的编码/换行不一致保留，修订使用Git二进制字面量保存需精确保留的文本/夹具。

隔离副本位于公共Git仓库内部时，先在当前PowerShell进程设置 `GIT_CEILING_DIRECTORIES` 为副本父目录，防止Git按外层仓库前缀跳过补丁路径；使用 `git -c core.autocrlf=false apply --no-index` 保持字节。未禁用外层仓库发现的旧检查不能单独证明应用成功；实际26文件哈希重放才是恢复证明。见[重放结果](../../OcctSharp/OcctSharp/artifacts/cadoryx-exact-offset-step-generated-31-draft-1/patch-replay-v2-2-verification.json)。应用source1068恢复仍使用原[应用补丁](../capability-evidence/AF_PENDING_SOURCE1068.patch)。

先完成非毫米单位及实际XDE/形状写入桥接，补充一般周期内部极值矩阵；再做两配置完整SDK、包和干净消费者门禁，接入source1068并验证原65/相关986/完整6532。只允许有实际区域来源和精确表示证明的路径获得应用导出能力，原不支持freeform的拒绝及已有目标文件/作者树/资产不变条件继续保持。
