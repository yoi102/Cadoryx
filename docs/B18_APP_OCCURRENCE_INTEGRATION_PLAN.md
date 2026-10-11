# B18：SDK26 完整当前出现路径的应用接入

2026-10-09。SDK26 已独立验收。应用已形成997来源的SDK26接入草稿：格式/包络、租约、独立分区、根句柄生命周期、显式当前选择、实际工作器传递及新的直接原生扫掠/放样入口已实现；新58实际发现并在两配置各58通过，相关两配置各728通过；完整Release、Debug各5860/5860已独立核对通过；当前候选的两轮性能/生产Open、材质组件和晚期导入DLL两故障已验，圆盘/通用窗口及60整图已独立验收。旧993/SDK25源码和已验WPF候选保留。A–F ACTIVE，G USER_DEFERRED。

## 保存表示

保留现有 geometry v2、`SerializedGeometryReferences`、OCRREF25/schema1 和旧迁移语义。新完整路径使用独立 `occurrence-references` v1 必需分区和 `cadoryx.native-occurrence-tables.1` 能力；分区按现有几何修订与 BRep 资产标识引用新的 OCROCC26/schema1 表资产。文档不含完整路径时不写该分区或能力，旧文件不自动生成路径证明。

`GeometryAssetRef` 增加可选的完整路径描述符。旧平面成员表与新完整路径表暂时互斥，因为 SDK25/26 分别 Capture 的 BRep 不保证相同 ShapeSet/位置登记或字节；不能把两个独立表按摘要、索引或几何相似性强行绑定。新 `StoreShapeWithOccurrences` 直接采用 SDK26 同一次 Capture 返回的 BRep 和路径表作为主要几何资产，所有者的原生报告只存在实际捕获期间。新分区存储表资产标识、格式与 schema，不存原来源或历史证明标志。

严格完整捕获入口为 `EvaluateGuidedForDocumentWithOccurrencesAsync`。当前1000来源新增显式 `GuidedDocumentEvidencePolicy`：WPF和真实CLI选择 `PreferCurrentOccurrences`，使实际扫掠/放样创作、编辑及C1重算的 `EvaluateForDocumentAsync` 保留同次Capture完整表；旧零/一/二参数构造器仍 `LegacyMembers`，临时预览仍普通1资产。可选捕获拒绝只保留普通有效几何并说明原因；严格入口仍拒绝超预算，Stage/回调/取消继续抛出并回收资产。含新分区writer0.7.9，其他文件writer0.7.8；完整历史组合与本版生产候选验收继续，不能称为全部交付。

分区映射只承担文档引用和完整性。读取表后必须由 SDK26 校验当前解码图的完整 containment paths、原生 token、类型及累计方向；GUID、路径整数、摘要和声明来源地址均不能代替原生身份。文件 Open 的原来源始终未知。

存储加载先执行有界托管包络/资产角色/完整性校验，不初始化OCCT；真正使用几何时，`ReadOccurrenceShape`/`ReadShape`调用SDK26实际解码并校验当前路径。不能把成功读到分区或通过哈希核对当作原生语义证明。新增反向token场景专门验证重算摘要仍不能绕过实际原生图校验。

## 生命周期与解码

`BoundaryAssets`、几何租约、资产目录和保存/加载同时持有主要 BRep 与路径表。Stage 的任一步骤失败、取消、回调抛错以及加载失败必须回收已拥有资产，不发布部分文档；继续使用原子保存。

`ReadOccurrenceShape` 返回 SDK26 独立拥有的 root/member/query 图。普通 `ReadShape` 不能沿用 SDK25 的直接转交 root 写法：SDK26 还有 feature-result 查询所有者。使用 `using` 释放完整 Restore 图前，经 `Resolve([])` 取得另一个实际根句柄，调用者独立拥有该句柄；验证返回根在查询所有者和原成员释放后仍可读取、测量和导出。Open 的预验证与 Restore 分列，不能描述为只有一次解析。

显式消费者首先确认当前文档、修订和资产仍匹配，再调用当前图的 `Resolve`、实际 `IsSame`/`IsEqual` 查询与 `ResolveUnique`。共享身份、多路径、错误方向、域外成员和未证明的复制对应继续拒绝自动重绑。新桥仅完成当前解码选择，不能称为历史组合已完成。

## 工作器与预算

独立工程图/螺纹工作器携带实际来源需要的完整路径表，并验证表集合没有缺失、重复或额外项目。保留原 drawing 256 instances、thread 3 primary assets、总传递字节和原期限。

SDK26 完整表限制512行、depth64、每份64 MiB。原草图256/512/80/depth64、装配32/128/100/5秒、体积1e-7和交换边界1e-5全部保持。路径预算约束证据，不能降低建模规模。现有大型建模继续保留普通几何并明确无完整路径，不发布部分表。取消检查不能宣称能中断正在进行的 OCCT 解析；完整恶意解析的进程资源隔离仍待证明。

显式要求完整捕获的桥在超预算时拒绝发布表；原普通建模/旧自动成员表路径保持可用。新增大compound场景核对失败后零资产、普通几何仍可保存及原1e-7体积，不把完整路径预算变成原建模的限制。

## 接入与验收顺序

1. 归档当前993源码和已验WPF候选，保持当前5802两配置及窗口/性能证据可追溯。接入已验SDK26本地包源，更新21个锁文件并严格锁包；writer/runtime基线和实际13托管/62原生一并审计。
2. 实现可选描述符、格式/有界包络、租约、独立分区、能力与资产角色校验。新分区缺能力、能力缺分区、无效schema、重复/域外修订、错误媒体类型、表损坏和旧分区冒充都必须拒绝；旧文件保持无路径证据。
3. 实现同次 Capture 保存与独立 Restore/根句柄生命周期。验证唯一、共享、正反方向、累计位姿、来源释放、另一解码图拒绝、超预算和 Stage 故障/取消；保留原几何断言和公差。
4. 接入实际独立工作器与当前解码显式选择消费者，验证工作器结果与直接原生结果一致、失败后所有租约归零。
5. 原5802逐名保留，新增场景分列；相关与完整Release/Debug、当前候选窗口/性能/材质/故障/原模型供应商流程按原条件验收。完整历史、材料、第三复制和跨作者组合随后继续，不把本接入阶段当作B18或A–F全部完成。

证据：[SDK26独立验收](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/occurrences-sdk26-verification-1.json)、[SDK26接口边界](B18_OCCURRENCE_REFERENCE_SDK26_DRAFT.md)、[SDK25应用契约](B18_APP_SERIALIZED_REFERENCES.md)、[当前993窗口验收](A5_CURRENT993_ACCEPTANCE.md)。

当前证据：[997构建/锁包/清单](../artifacts/b18-app-sdk26-build-2-verification.json)、[Release58](../artifacts/b18-app-sdk26-release-focused58-1-verification.json)、[Debug58](../artifacts/b18-app-sdk26-debug-focused58-1-verification.json)、[Release728](../artifacts/b18-app-sdk26-release-related728-1-verification.json)、[Debug728](../artifacts/b18-app-sdk26-debug-related728-1-verification.json)。

完整回归之后的当前候选验收脚本已[准备并解析](../artifacts/a5-current997-acceptance-static-review-2-verification.json)。这是准备时记录；冻结997候选、窗口/性能已经验收，供应商两项仍FAIL；[负例门禁](../artifacts/a5-current997-incomplete-runtime-guard-2.json)已实际核对缺完整5860证明时拒绝后续执行。首静态供应商版本常量错误与首负例夹具错误记录保留，修正不改原模型、提示、预算或几何断言。

## 当前全量门禁与接续

当前997来源的[完整Release5860](../artifacts/b18-app-sdk26-release-full5860-1-verification.json)已通过，零失败、零跳过，31.9928分钟。原5802测试源码和断言全部保留，新58分列。[完整Debug5860](../artifacts/b18-app-sdk26-debug-full5860-1-verification.json)也已独立核对通过，32.4861分钟；实际原生载荷仍为包内Release版本。[当前WPF候选](../artifacts/a5-current997-app-build-verification-1.json)已发布并绑定测试共享17/SDK13/62，冻结997两轮direct/生产Open、圆盘/通用窗口、材质组件和晚期导入DLL门禁均已完成，本版1000的生产候选门禁仍待验。

[当前材质与导入故障准备](../artifacts/a5-current997-material-fault-static-verification-1.json)已解析，实际负例门禁通过；材质仅修改受验运行库身份，保留原21项、76完整PNG和9独特画面字节要求，导入故障控制器源文件逐字节保持，仍按原240秒验证缺失/无效DLL工作器与父文档保留。这不证明应用材质界面或全部进程内渲染/GPU故障。

下一组消费者验收已形成[9场景草稿](../artifacts/b18-occurrence-consumer-draft-1/preparation-1.json)：基准面/轴/点和草图投影的当前与过期来源，保存关闭后全新普通/延迟重开；真实螺纹工作器接受完整路径表、与直接几何核对；缺表/重复/额外/损坏表在原生建模前拒绝。该9场景随后作为1000来源接入，与40策略场景一起在两配置各49及相关各777通过。首过期来源夹具类型错误的47/2 FAIL保留；修后同类型导入更新使原过期/撤销/重开断言全部成立。这组不证明完整拓扑历史。

冻结997候选验收后，实际创作与重算的显式策略及上述深层消费者已接到1000来源并通过专项/相关回归；超预算应保持既有普通建模规模，同时明确没有完整路径证据。旧成员表与同次Capture完整路径表继续互斥，不能按哈希或几何相似性拼接。后续完整历史/材料/第三复制/跨作者组合和原模型工程图失败仍逐项完成。

创作入口的[显式策略草稿](../artifacts/b18-guided-occurrence-policy-draft-1/preparation-2.json)现已形成五个已有文件的独立修改和新策略类型：WPF和真实CLI显式选择完整当前路径，零/一/二参数旧构造器保持成员表策略，瞬态EvaluateAsync保持普通1资产。捕获与资产Stage/回调分开，同一次Capture提供BRep和表；仅Capture的ArgumentException允许保留普通几何并记录实际拒绝原因，不把所有拒绝都叫预算超限。取消、Stage与回调错误继续抛出并回收全部资产。该草稿随后应用到1000来源，40策略加9消费者全部实际发现，新49和原728加49的相关777两配置已通过；首新夹具类型错误的47/2 FAIL保留。997已完整归档，当前1000完整5909与新候选门禁继续。

当前候选的[性能](../artifacts/a5-current997-performance-verification-1.json)、[材质组件21/76](../artifacts/a5-current997-material-verification-1.json)与[实际晚期导入DLL两故障](../artifacts/a5-current997-late-import-fault-verification-1.json)已独立核对通过。当前原模型[工程图](../artifacts/f5-current997-lmstudio-drawing-1-verification.json)及[多文档](../artifacts/f5-current997-lmstudio-workspace-1-verification.json)均仅完成两轮，第三轮耗尽原16轮；缺文档状态字段及重复读取/保存仍需修复，不能追认旧993的供应商8轮。当前圆盘/通用窗口已验，997来源完整归档，见[验收与归档](../artifacts/b18-app-current997-source-freeze-1.json)。

当前1000的[两配置构建/5909清单](../artifacts/b18-guided-policy-build-3-verification.json)、[Release49](../artifacts/b18-guided-policy-release-focused49-2-verification.json)、[Debug49](../artifacts/b18-guided-policy-debug-focused49-2-verification.json)、[Release777](../artifacts/b18-guided-policy-release-related777-1-verification.json)及[Debug777](../artifacts/b18-guided-policy-debug-related777-1-verification.json)均已独立核对。基准面/轴/点和草图投影的当前/过期来源、普通/延迟全新重开以及真实螺纹工作器的正确/错误表组合成立；不追认导入原来源或完整历史。原模型反馈改进为[独立两文件草稿](../artifacts/f5-current1000-provider-feedback-draft-3/preparation-1.json)，未应用、编译或运行，模型与原预算不改。

当前1000的[发布/性能/圆盘和通用窗口准备](../artifacts/a5-current1000-acceptance-preparation-2.json)已通过Python/PowerShell解析，原条件保持；[实际缺全量拒绝](../artifacts/a5-current1000-incomplete-runtime-guard-2.json)证明没有完整5909两配置结果时不会创建候选或启动原生/键鼠动作。首原清单属性映射错误保留为独立准备失败，修正不改生产代码。当前Release完整沿原PID37532继续，Debug完整尚未开始。新的AI两文件及[27场景草稿](../artifacts/f5-current1000-provider-feedback-draft-4/preparation-1.json)仅源码审阅，未应用/编译/运行，不属于当前5909清单，也不改变原供应商FAIL。

当前1000的[完整Release5909](../artifacts/b18-guided-policy-release-full5909-1-verification.json)已独立核对通过，0失败/跳过，运行约34.10分钟；[完整Debug](../artifacts/b18-guided-policy-debug-full5909-1.log)沿同一session31929串行启动、实际PID67824与62已捕获，尚无终态。当前[缺Debug全量门禁](../artifacts/a5-current1000-incomplete-debug-guard-3.json)仍在发布/原生/键鼠前拒绝，不把已验Release当两配置通过。

2026-10-10：原1000两配置完整5909、性能与窗口均已验并独立归档。当前视口使用完整表描述缓存键及全部边界资产租约，来源样式路径也验证补充表实际解码；修复前48为12/36、同一程序修复后48全部通过，Source/BRep两个独立解码域不据此成为组合历史身份。当前修复版完整5909运行中，材料/第三复制/跨作者仍未证明，见[引用缓存记录](A5_REFERENCE_CACHE_LIFETIME_PLAN.md)。
