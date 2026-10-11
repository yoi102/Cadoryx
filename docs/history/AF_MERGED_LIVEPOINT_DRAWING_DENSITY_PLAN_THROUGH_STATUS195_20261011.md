# 历史快照：AF_MERGED_LIVEPOINT_DRAWING_DENSITY_PLAN.md（截至 status195）

归档于 2026-10-11；以下保留当时过程记录，不作为当前完成或运行状态。当前状态见[暂停检查点](../AF_CHECKPOINT_2026-10-11.md)。原文件 SHA-256：`956254ACB5426EF28159EF102D3DEA7AC303F5E8964E7A278B09762A8D8864AA`。相对 Markdown 链接已按归档目录调整。

---

# A–F：实时点、绘图检查与密度显示合并验收

2026-10-11，status190。A–F ACTIVE，G 暂缓。正式子批进度保持 A4 / B17 / C5 / D5 / E4 / F5g；本页记录当前候选的验证，不代表全部完成或生产接入。

当前生产为缓存修复基线1000 / SDK26。新隔离来源1056保留原6392项源码与断言，加入30项绘图检查反馈验证。完整清单两配置均为6422；Release和Debug构建各0警告/错误，联合350项各350通过，SDK13及实际原生62绑定。[源码冻结](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/preparation-3.json)、[两配置联合独立核验](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/build-focused-both-3-verification.json)。

## 本次实现与边界

| 内容 | 当前结果 | 仍需完成 |
| --- | --- | --- |
| B18实时生成点链 | 原270项全部保留：216实际求解/双实体/存储、36完整Jacobian、18冻结截断；1/3/7层依赖均已专项验证 | 完整6422两配置及C1的18流程/18文件/54整图已通过；真实点编辑键鼠、同环混合接头及全过程原生历史继续 |
| 绘图检查参数反馈 | 精确documentId/stateId校验之后，缺失或无效type返回允许类型及完整参数要求；空文档未知类型也拒绝。只读错误不改历史，不填补身份或type | 当前原Bonsai工程图2轮/30调用、跨文档5轮/32调用后均耗尽工具轮，完整八轮尚未通过 |
| 上下文预算 | 压缩说明后保留全部12工具、8192上下文、1024输出及原安全预算；schema结构和旧测试不改 | 完整6422两配置已通过；原供应商工作流仍失败 |
| 密度显示 | 已验的G15单处XAML改动合入本候选；独立实际窗口显示7850，原始浮点、物理密度和文件保持 | 当前候选应用窗口及生产接入 |

绘图允许检查类型为sheet、template、view、dimension、datum、control_frame、balloon、hole_table、field、note。parts_table和zones保留其既有作者契约，但不是检查类型。跨文档或过期状态先拒绝，不泄露类型指南；未知外层字段保持原拒绝。原6122、B18新增270与反馈新增30的名称和断言完整保留。

## 当前完整与窗口门禁

[完整6422冻结](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/full6422-preparation-1.json)已生成；Release/Debug完整各6422/6422、零失败/跳过已独立通过，源/逐名/实际13与62绑定。[两配置终态](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/full6422-both-1-verification.json)。每配置必须完成全部6422、零失败/跳过、原逐名清单、冻结载荷和实际62库核验。旧B18完整6392准备保留，通过本次合并全量可覆盖相同6392；不会同时重复启动两套全量。

[C1专用WPF构建与载荷核验](../../artifacts/b18-live-point-window-draft-2/build-2-verification.json)已通过两配置0/0和当前测试载荷17/13/62。它增加隔离诊断入口，未修改生产源码。计划三类曲线、两个依赖方向、三种实际语言，覆盖1/3/7层链；每流程保留完整来源坐标、区域距离与中间偏移的参数驱动、循环拒绝、预览失效、取消/确认、一笔撤销重做、只读区域距离和生产保存重开。原240秒、256变量、512方程、80次迭代、深度64和体积1e-7保持。三族各6、总18实际WPF绑定/Automation Invoke/诊断画布流程、54张完整PNG直阅和18个完整18分区保存文件独立核验已通过；这不代表真实键鼠点编辑。

当前候选应用与供应商消费者均已构建并独立核验，完整6422两配置前提已满足。[应用发布核验](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/app-build-1-verification.json)。原模型prism-ml/bonsai-27b、localhost:15630/v1、8轮工作流、16工具轮、8192及6000/2400结果预算保持；三份C#提示和检查逐字节不改。[供应商准备](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/provider-preparation-1.json)。

## 保留的失败

- [首轮构建失败](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/build-focused-both-1-exit.json)：新增测试缺少MemoryAssetStore命名空间；第二版只补引用。
- [第二轮联合失败](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/focused-release-2/focused-release-2.trx)：348通过/2失败；说明增量使严格预算模式裁掉第12工具。第三版只压缩说明，原预算、schema形状和所有测试保持。
- [原SDK27 Bonsai工程图失败](../../artifacts/a5-sdk27-control-unified-draft-1/lmstudio-drawing-1-verification.json)：完成1轮、22调用，第二轮耗尽16工具轮，7个参数错误。
- [原SDK27 Bonsai多文档失败](../../artifacts/a5-sdk27-control-unified-draft-1/lmstudio-workspace-1-verification.json)：完成7轮、43调用，第8轮空回复；过期expected仍严格拒绝，资产归零。
- [C1首轮编译失败](../../artifacts/b18-live-point-window-draft-2/build-1-exit.json)：诊断夹具缺少已公开SketchRegionEvaluationContext的命名空间；修正版只补引用。
- [C1审计器路径失败](../../artifacts/b18-live-point-window-draft-2/build-auditor-2-failure.json)：标签替换误改了未变的基线app-build-1路径；新独立审计器恢复原基线，不重构建或改变断言。

## 自动恢复接续

已准备[SDK28原生健康状态源码与12测试](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/overlay-preparation-1.json)，实际两配置原生/托管构建0/0、导出+2/API+8及各12专项已通过，20整PNG已直阅；SDK28完整两配置各1358已独立通过。它读取OCCT显示数据丢失、共享上下文不存在/无效的复制标志，并提供调用方明确标记显示数据重传的入口；不暴露原生对象，不清除读取标志，不猜测物理GPU移除。普通几何异常不会被转换为图形失效。

SDK28实际两配置原生/托管、旧1346+新12、完整1358、线程/C-ABI、导出/API及20整图已验；实际缺失/无效上下文、适用的包/消费者及WPF门禁继续按当前证据分列。WPF有界合并恢复、双视口、关闭竞争、重建失败、后续建模保存与零资产未完成。新[原生消费者串行守卫](../../artifacts/assert-cad-native-consumers-idle-2.ps1)已用于本次构建/测试调度；针对dotnet托管原生工作流的新增识别已在原工程图实际62活进程上核验并独立审计，证明记录时旧守卫IDLE、新守卫拒绝确切PID且进程保持。

旧SDK27两配置6122、材质82/346、实际材质picker/G15、圆盘/窗口和MiniPC3性能证据继续按各自冻结载荷保留，不直接计为新1056候选的当前应用验收。G实机矩阵、安装和交付暂缓，不删除A–F自身所需的实际窗口、外部CAD和输入证据。

2026-10-11 status184 接续：当前[完整6422串行入口](../../artifacts/run-af-sdk27-merged-full6422-1.ps1)仍在运行，没有完整终态；[后续串行入口](../../artifacts/run-af-sdk27-merged-after-full-1.ps1)等待两配置独立通过后才发布当前应用、构建原供应商消费者并执行原Bonsai两组工作流。任一全量失败禁止后续消费者启动；供应商某组实际失败保留后仍独立审计另一组。该入口不启动桌面输入，不应用生产。

[C1当前运行准备](../../artifacts/b18-live-point-window-draft-2/runtime-preparation-2.json)已绑定编译入口、载荷及原240秒；首版通用标签替换误删coupled在静态检查时发现，修正版在任何窗口启动前仅纠正旗标，不记为窗口运行失败。原首版和全部条件保留。18个实际WPF控件绑定/Automation Invoke流程与真实键鼠路径分列，桌面阶段继续使用sky，并在开始前提示。

[WPF单次自动恢复源码草稿](../../artifacts/a5-wpf-render-health-recovery-draft-1/preparation-1.json)已准备：只根据确切当前视口的已处置原生所有者或原生显示数据丢失/上下文无效标志请求恢复；重复请求合并，每次外部附着至多一次，关闭或换文档使待执行请求失效。计划清除旧视口的动画、圆盘、捕获和渐进回调，再从当前文档快照/视图状态与相机重建，不重放建模命令。该草稿尚未编译；有效上下文消失的分类、相机缓存完整性、渐进加载清理前取样、准备期限/失败清理、三语言资源和全部实际故障矩阵仍待补齐。30秒是非阻塞准备状态观察，不能称为中断原生调用的硬期限。生产保持1000/SDK26，自动恢复未验收，A–F ACTIVE，G暂缓。

2026-10-11 status185 接续：[Release完整6422](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/full-release-6422-1-verification.json)已独立通过，全部6422、零失败/跳过，46.4894分钟；Debug沿同一完整入口运行中，不因等待重新启动。当前发布和Bonsai工作流仍要求确切完整两配置；SDK28构建另行排在原headless终态之后，所有原生消费者保持串行。

[C1独立存储审计](../../artifacts/b18_livepoint_saved_audit_1.py)及[运行核验入口](../../artifacts/verify-b18-live-point-window-runtime-1.py)已准备，冻结源1058/当前build2/应用17/SDK13/原生62/runner2预检通过。审计读取完整18分区的MessagePack，不调用产品反序列化器；逐绑定核对坐标处方、区域距离、每个可变中间偏移和确切1/3/7层来源链。新共享点由末级实时表达式声明，沿明确Source.Entity到指定区域输出；只有两条源消费者边，按新结构核对，不冒用旧直接区域共享点的四条边。原接头顺序/桥接/反向检查完整保留。运行时需保留240秒、完整54图直接审阅、18文件和三组实际62证据；当前尚未运行，正/负存储变体核验也继续待真实首组文件。该诊断的控件/Automation Invoke/Canvas输入与真实sky键鼠验收分列。

[恢复第二版](../../artifacts/a5-wpf-render-health-recovery-draft-2/preparation-2.json)为8文件隔离来源：明确记录曾观察到有效上下文，初次缺上下文不触发；补成功导航、缩放、投影、恢复、场景呈现后的相机缓存；把清理前确切视口图形观察附于原异常，原异常类型保持。30秒观察计时在Attach前启动，当前文档/epoch/视口/实际健康和场景就绪共同决定完成；部分重建错误延后回收确切旧宿主，退休时清理错误不触发第二次恢复。三语言资源只加resx，原生成Designer未修改。源码转换预检曾发现OnReady条件在三个方法重复，写入前已限定到完整方法上下文；未启动窗口，不记为运行失败。当前仅准备并核对资源一致性，编译及全部双视口/关闭/失败矩阵仍NOT RUN。

[SDK28工作区1031](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/workspace-preparation-1.json)保留原1026来源及原1346全部名称，只更新既有ABI/bridge版本期望并加原生健康12场景。生成文件未手改；类型转发按原生成器刷新。实际1358完整回归、真实原生健康语义/图像、线程/生命周期、包和公开消费者均NOT RUN。[两配置构建入口](../../artifacts/run-a5-sdk28-build-1.ps1)及[独立核验](../../artifacts/verify-a5-sdk28-build-1.py)已准备和语法核对，[串行接续](../../artifacts/run-a5-sdk28-after-merged-headless-1.ps1)正在等待原完整/应用/供应商流程终态，没有提前编译。[本次源码准备核验](../../artifacts/af-merged-next-preparation-1-verification.json)不表示SDK28运行或WPF恢复验收。生产保持1000/SDK26，正式子批保持A4/B17/C5/D5/E4/F5g，A–F ACTIVE，G暂缓；桌面阶段继续开始前提示。

2026-10-11 status186 接续：Release6422保持独立通过，原Debug6422继续，没有两配置终态。当前应用、供应商与SDK28继续按已准备串行入口等待，不重启原全量。

[恢复第3版](../../artifacts/a5-wpf-render-health-recovery-draft-3/preparation-3.json)将原Detach每个事件解绑、动画/圆盘/捕获/渐进/宿主回收独立尝试，某一步失败不跳过其他清理。已知已处置的原生所有者读取旧相机；取消输入不增加退休时的相机/上下文查询。动画catch保留同次发起的宿主与视口。源码审查发现Renderer可把其他清理错误附于首个已处置异常；[当前第4版](../../artifacts/a5-wpf-render-health-recovery-draft-4/preparation-4.json)遍历完整已知异常图，按引用检测循环，任何其他错误阻止恢复。原第2/3版和全部准备保留。当前8文件和SDK28合计[9份C#语法0错误](../../artifacts/af-next-source-syntax-2-verification.json)，不代表语义编译；已处置异常的实际ObjectName、双视口/关闭/重建失败/继续建模/撤销/保存重开及零资产仍必须实际验收。

[SDK28原生运行工具](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/runtime-tools-preparation-1.json)已准备：Release/Debug分别12专项与原1346+12=1358全量，精确逐名、全部断言、对应实际原生62/托管13及隐藏窗口完整PNG。它们尚未运行。只读原生状态是OCCT初始化/显示数据标志，不是物理GPU移除诊断；当前12计划不能替代真实上下文缺失/无效和WPF故障矩阵。上游根docs已记录[手工例外/所有权/状态](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/contract-docs-1-verification.json)，公开BQ源码/交付状态保持，私有SDK28仍未编译或接入。

[当前圆盘/通用4工具](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/window-tools-preparation-1.json)按当前合并app/full6422载荷准备，原7采样/400ms/60秒前台准备/180与240秒/12循环及完整60图保持，尚未运行；当前MiniPC3及材质82/实际材质/G15也须按新载荷另行复验，旧结果只属其冻结载荷。实际[两窗口启动负例](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/window-missing-full-1-verification.json)及[C1启动负例](../../artifacts/c1-window-missing-full-2-verification.json)确认缺两配置完整6422会在窗口/原生/输入前拒绝；[独立核验及SDK28缺构建负例](../../artifacts/af-next-missing-prerequisites-1-verification.json)也已实测。未生成窗口结果，不把拒绝当运行FAIL或功能PASS。

[原供应商串行守卫只读观察器](../../artifacts/observe-merged-provider-idle-guard-1.ps1)已排队等待实际62且仍活跃的原dotnet消费者，才比较旧IDLE/新拒绝确切PID并复核同进程仍存活。它不启动、重启或停止消费者；尚无正例，不计为守卫已验。生产仍1000/SDK26，正式A4/B17/C5/D5/E4/F5g，A–F ACTIVE，G暂缓；桌面尚未开始，开始前提示保持。

2026-10-11 status187 接续：合并1056的[Release/Debug完整各6422](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/full6422-both-1-verification.json)与[当前应用](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/app-build-1-verification.json)已独立通过。App Debug使用包内Release原生SDK27；SDK28的真实Debug原生另验。

当前原Bonsai[工程图2轮/30调用](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/lmstudio-drawing-1-verification.json)、[跨文档5轮/32调用](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/lmstudio-workspace-1-verification.json)后均在下一轮耗尽原16工具轮，实际62与零资产已验，均FAIL。跨文档重复缺partId的timeline和缺type的model_contract仍需修复反馈与实际复验；不自动补身份或放宽预算。[审计加载竞争](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/provider-auditor-race-2-verification.json)保留旧审计器已加载后失败，当前按冻结完整说明审计同一工程图结果，未重跑；其实际记录更正了早先准备记录的时序判断。

[守卫实际正例的独立审计](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/provider-dotnet-idle-guard-1-independent-verification.json)绑定原PID52832、相同62模块与消费者构建；证明原观察前后同进程保持，不声称现在仍存活。当前SDK28[新串行接续](../../artifacts/run-af-merged-after-auditor-repair-2.ps1)首次实际构建运行中，[运行接续](../../artifacts/run-a5-sdk28-runtime-after-build-2.ps1)等待构建独立通过后再做两配置12专项与1358全量，隐藏原生窗口，不发键鼠。旧两个排队流程因原审计失败在构建/运行前退出，其结果保留。

[当前材质82消费者准备](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/material82-1/preparation-1.json)保持原Program逐字节、82场景、346整480×320图及零资产；当前圆盘/通用窗口与MiniPC3仍保持原时间和像素条件，尚未运行。[C1语义变体核验](../../artifacts/verify-b18-livepoint-saved-mutations-1.py)待实际首组6文件，独立完整重编码与72个有效校验和但语义错误的变体，原文件名/目录机制已静态修正；仅工具源码准备。恢复第4版仍未语义编译，完整故障矩阵继续待验。生产1000/SDK26及正式A4/B17/C5/D5/E4/F5g保持，A–F ACTIVE，G暂缓。桌面验收在原生队列结束后开始，开始前提示。


2026-10-11 status188：SDK28[同一构建独立核验](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/build-1-verification.json)、[原守卫失败及纠正](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/post-build-auditor-repair-1-preparation.json)和[20当前整图](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/images-1-verification.json)已保留。新枚举由facade直接定义，原4176生成转发保持逐字节，不应自转发。SDK28完整两配置各1358已独立通过；显式SetDeviceLost重传测试不代表真实上下文缺失/无效或物理GPU移除。[运行接续3](../../artifacts/run-a5-sdk28-runtime-after-build-3.ps1)只在同构建独立通过后首次运行原12/1358；旧运行接续2在所有测试前拒绝，记录保留。私有包/公开消费者/WPF实际恢复继续。

[F5只读检查角色草稿](../../artifacts/f5-inspection-role-feedback-draft-1/preparation-1.json)根据当前跨文档实际重复错误补两处提示：timeline先显式parts发现，把所选items[].id放partId，id仍只筛选该part中的feature；model_contract的id必须是既有16类型名，不是GUID。全部原6422/source/schema/说明与模型预算保持，新26检验严格拒绝、显式纠正重试及状态/历史/选择/资产不变。[三源码语法](../../artifacts/f5-inspection-role-feedback-draft-1/syntax-1-verification.json)仅为解析证据，[两配置影响构建工具](../../artifacts/f5-inspection-role-feedback-draft-1/build-tools-preparation-1.json)已准备，未编译或执行。C1原运行审计器路径参数改为Path对象，纠正与守卫审计同类Python类型问题；窗口/保存语义还未运行，断言/预算不改。


## status189 当前验收

[C1终态](../../artifacts/b18-live-point-window-draft-2/runtime-all-1-verification.json)为三曲线族×两方向×三语言的18流程，全部原参数、预算、体积1e-7、预览取消/确认、一笔撤销重做、保存关闭重开和零资产要求保持；54张原完整图均逐张直接审阅。[独立存储工具](../../artifacts/b18-live-point-window-draft-2/saved-auditor-mutations-2-verification.json)实际6文件、6完整重编码和72有效校验和语义破坏拒绝，工具枚举kind3误用/修正kind4的原失败与原文件保留，没有重跑已通过窗口。产品解码拒绝及真实点编辑输入另验。

[当前窗口图像](../../artifacts/af-merged-window-images-1-verification.json)的11张变化通用整图与2张圆盘整图逐张直阅，49张通用整图与原已验完整字节相同；14前台静止采样、鼠标不改页和12窗口循环均已通过，原400ms/180或240s/60s等待保持。原生排队滚轮到可见像素证据不代表物理滚轮硬件延迟。[当前材质](../../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/material82-1/images-1-verification.json)保持原82断言，346张完整480×320图与接受基线逐字节相同；当前picker/G15桌面另列。

[当前性能](../../artifacts/af-merged-performance-verification-2.json)：MiniPC3两新进程、各20双视口循环通过，首次完整提交4.752 / 5.061秒，原6.482秒目标PASS；两次实际Open通过原完整788×411图像校验，打开导入耗时与direct提交分列。88,258,425字节固定MiniPC3、4964实例/1185几何、实际62模块、原1800/900秒截止与所有完整像素要求保持；这些CPU提交及打开耗时不是屏幕FPS、GPU利用率或物理输入延迟。

F5检查角色反馈1057两配置构建0/0、发现原6422+新26=6448、影响专项各188通过，实际13/62绑定；完整6448和原供应商尚未运行。SDK28私有14包与新缓存公开消费者两配置通过，保留原42/28/32/initial10并新增5族健康/重传及所有者拒绝；实际disposed ObjectName=OcctSharp.OcctViewer。消费者整图审计另列。WPF恢复仍为第4版源码，真实缺失/无效上下文与双视口/关闭/重建失败、后续建模保存重开和零资产矩阵继续；生产1000/SDK26及正式子批不推进。A–F保持ACTIVE，G用户暂缓；桌面键鼠已释放。

首轮当前Open完整图因1066个青色轮廓差异FAIL，[原失败](../../artifacts/af-merged-production-image-failure-1-verification.json)保留；新输出2维持同一候选、两新进程、原900秒/完整基准PNG，桌面指针只停在实际观察到的视口外位置。不重跑已通过的两次direct或改变图像要求。


## status190 SDK28公共边界与恢复集成

[SDK28私有14包](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/pack14-1-verification.json)绑定已测Release托管13/原生62、钉定版本、许可证及新增当前手工契约快照；历史SDK26文档原字节保留，不声称全部文档都是当前状态。[新缓存公共消费者](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/public-consumer-2-verification.json)两配置保留原42历史/28同图序列化/32实例/10显示模式，五类形状/标签/实例/来源/样式来源的健康只读、显式数据重传、另一视口隔离及线程/处置拒绝均已验。[完整60图](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/public-images-1-verification.json)含20健康和40显示模式，均与已接受原320×320整图逐字节相同。消费者两配置均使用包内Release原生；SDK自身实际Debug1358另验。

首个公开夹具误用Id/IsVisible/IsRemoved的[8条CS1061](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-28-draft-1/public-consumer-1-compiler-failure.json)保留，没有SDK API扩展或包重打；consumer2从公开原生选择取得同一managed wrapper、清除选择并通过Hide/Show完整像素往返，证明公开身份与可见性。原SDK12专项中对内部Id的断言保持。准备器误数9而实际8的前置失败也保留，尚未复制/运行时纠正，不覆盖证据。

F5角色反馈1057两配置0/0、影响各188与发现6448通过；当前完整6448已沿原串行入口启动，尚无两配置全量终态，原模型工作流未复验。[完整6448准备](../../artifacts/f5-inspection-role-feedback-draft-1/full6448-preparation-1.json)保留原6422及新增26逐名、未过滤运行、实际62与旧预算；当前Release原生模块已捕获，不因等待重启。

[SDK28+恢复第4版+角色反馈1059集成](../../artifacts/af-sdk28-recovery-role-draft-1/preparation-1.json)复制完整当前1057，加8份已核对草稿（2新文件）并更新包源/版本/运行基线；原6448全部测试文件与Designer/schema保持。旧SDK27锁文件等待真正restore刷新后再冻结，当前未编译/运行，不将来源准备当作自动恢复已验。实际disposed所有者名称与已准备的精确异常判定相符；上下文缺失/无效与双视口/关闭/重建失败、后续建模保存重开/零资产矩阵仍待验。

当前MiniPC3首次完整CPU提交4.752/5.061秒，实际点击打开至完整场景65.376/66.559秒；原生导入仍约58秒，两者含义保持分列。生产1000/SDK26、正式A4/B17/C5/D5/E4/F5g及原供应商失败保持，A–F ACTIVE，G暂缓，键鼠已释放。

## status191 OcctSharp 生成器承接

用户明确提醒新封装必须考虑代码生成器。SDK28的手工桥接、全部测试和公共消费者作为冻结候选保留；它们不能证明生成器可以重建封装。SDK28加恢复的旧应用构建入口没有启动。新[SDK29准备](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-1/preparation-1.json)复制原1031来源后移除三份手工底层文件，增加生成器、模板/契约及11条规则测试，共1034来源。原175生成文件及原全部测试逐字节保持；三份新生成输出还不存在，不能记为生成已通过。

`ViewerSupportWorkflowEmitter`由标准generate入口调用，配置显式选择`render-health-v1`，四份OCCT 8.0.1头文件逐字节哈希校验。未知/重复规则、版本或头文件变化会在写出前拒绝；不公开借用的图形上下文、驱动或结构管理器。模板生成C ABI头/实现和当前NativeMethods调用，保留原interop程序集和高层门面的线程/ABI/未知标志检查。支持胶水生成不增加已迁移OCCT声明StableId；现有Manual/Emitted覆盖统计不能因此提高。

[四文件语法](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-1/syntax-1-verification.json)通过仅证明解析。已启动的[串行入口](../../artifacts/run-sdk29-viewer-generation-after-full-1.ps1)等待当前F5完整两配置6448独立通过，之后才编译生成器、运行两配置规则/原套件及实际两次生成。原175文件、StableId、完整manifest/coverage/diagnostics/closure和模板溯源必须保持或具有准确的3文件增量。实际原生/托管两配置构建、导出/API无增减、1358语义/生命周期、公用消费者及WPF上下文故障矩阵继续，不将生成成功当作编译或运行通过。

A–F继续；生产1000/SDK26及正式A4/B17/C5/D5/E4/F5g保持，G用户暂缓。桌面输入未开始，开始前提示保持。

## status192 SDK29 应用来源与原供应商接续

独立[SDK29恢复/角色1059](../../artifacts/af-sdk29-recovery-role-draft-1/preparation-1.json)复制原SDK28准备，不改变八份恢复4源码和全部原6448测试。仅版本、私有源和运行基线改变；`nativeSha256`暂为空，明确禁止把SDK28的实测哈希当作SDK29。须在真实生成/原生构建/1358、私有14包和公共消费者通过后，使用实际包内Release原生哈希并刷新/冻结锁文件，再编译和验收恢复。来源准备不代表自动恢复通过。

[F5原供应商准备](../../artifacts/f5-inspection-role-feedback-draft-1/provider-preparation-1.json)保持原三份C#流程，只有项目引用路径和原SDK27 props迁移至已测角色1057。原两组八轮、Bonsai模型、endpoint、16工具轮、8192上下文、6000/2400结果预算和所有输出/关闭资产断言保持。实际models只读查询仍列出原模型；这不是推理或工作流通过。新的[串行供应商入口](../../artifacts/run-f5-role-provider-after-sdk29-lane-1.ps1)已启动等待现有SDK29原生队列结束；若SDK29失败，F5自身仍须满足当前两配置完整6448和原生空闲，独立继续，不把两个候选的成功条件混用。两组工作流各运行一次，某组功能FAIL保留后仍独立审计另一组；审计自身拒绝则停止后续并保留诊断。

当前完整6448 Release的确切dotnet39420/testhost35424仍活跃，日志与实际62捕获保留，Debug串行跟随；不因等待重启或放宽要求。SDK29生成/编译/运行及当前F5推理仍NOT RUN。生产与正式子批保持；A–F ACTIVE，G用户暂缓，键鼠未占用。

## status193 当前完整回归

当前角色反馈1057的[Release完整6448](../../artifacts/f5-inspection-role-feedback-draft-1/full-release-6448-1-verification.json)已独立通过，零失败/跳过，42.6894分钟。原6422+新增26逐名、来源/载荷和实际13/62均保持。Debug阶段因另一个OcctSharp生成器/BR实际Debug消费者临时占用而等待；没有终止或修改它们。占用结束后原Debug首次启动，dotnet53232/testhost52948和实际62捕获已观察，尚未完成。SDK29生成、原生构建、1358和F5原供应商队列依旧等待两配置完整门禁；不把Release单配置当作两配置通过或A–F完成。

SDK29恢复1059明确等待新原生哈希和实际包/锁证据；原供应商模型只读列表观察见[原Bonsai仍列出](../../artifacts/f5-role-bonsai-models-observation-1.json)，不代表推理成功。生产1000/SDK26、正式子批和原供应商失败保持。A–F ACTIVE，G用户暂缓，键鼠未占用。

## SDK29 私有包与干净消费者接续

[新干净消费者来源](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-1/public-consumer-preparation-1.json)保持SDK28已验的全部C#源码字节，包括42历史、28序列化、32实例、10显示模式和五族健康/重传断言；仅项目包版本改为SDK29，不增加公开API或放宽条件。两配置消费者均使用包内Release原生，SDK自身实际Debug1358继续单独验收。

[私有包/消费者串行入口](../../artifacts/run-sdk29-package-public-after-role-provider-1.ps1)已启动等待当前原供应商原生队列结束，再要求SDK29实际生成、两配置构建、各12/1358独立通过及20张完整健康图像审计。打包准备将复制当前生成契约为新名称，保留SDK28历史快照；包内容核对实际13托管/62原生、钉定依赖和许可证。随后通过新缓存、私有源与锁定还原运行原消费者，并独立审计完整60图来源。公开消费者整图审阅与WPF故障矩阵仍独立待验；不把代码生成、打包、进程返回0或旧图像当作完整产品验收。

[两项实际缺前置条件拒绝](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-1/missing-package-gates-1-verification.json)确认缺打包准备/包审计会在包、缓存、NuGet配置、发布和原生运行之前拒绝；未产生这些副作用。这是工具入口拒绝证据，不是包或消费者功能PASS。当前Release6448已通过、Debug仍运行，A–F与原预算保持，G暂缓。

SDK29恢复的[实际哈希冻结入口](../../artifacts/freeze-af-sdk29-native-stamp-2.py)仅在真实生成/原生/1358/包14/公共60整图和角色两配置6448通过后更新运行基线，原SDK28哈希不会沿用。[真实锁冻结](../../artifacts/freeze-af-sdk29-restored-locks-3.py)要求还原日志无警告/错误、14个新包内容SHA512与全部锁条目精确一致，且所有非SDK已解析依赖保持原条目；只允许锁文件变化。当前1059源码守卫已实际通过，恢复八文件和原全部6448测试保持。该守卫证明来源一致，不证明语义编译或自动恢复。

[SDK29恢复两配置编译队列](../../artifacts/run-af-sdk29-recovery-role-build-after-public-1.ps1)已启动等待私有包/公共消费者入口结束；先独立审计公共60整图，再冻结实际原生哈希、刷新与锁定还原并两配置构建/发现6448。任一前置失败会拒绝下游，保留原失败；当前没有开始还原/编译，没有新的WPF故障或桌面输入结果。实际源检查、脚本语法、编译、原生运行、完整窗口图像和产品完成继续分列。

## status194 生成器修订及原模型接续

当前[角色反馈完整两配置6448](../../artifacts/f5-inspection-role-feedback-draft-1/full6448-both-1-verification.json)已独立通过，Release42.6894、Debug41.5112分钟，零失败/跳过。原6422及新增26、来源和实际13/62绑定保持，不重复已通过的完整回归。

[SDK29首生成器失败](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-1/generation-build-1-exit.json)发生在新增规则测试编译，CA1861要求复用常量数组。新的[draft2](../../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-2/preparation-1.json)只改这一测试的数组存储形式，断言/11用例不变；生成器、模板、ABI/API与原175输出保持，来源守卫已实际通过。不会手改生成输出或给支持胶水增加声明迁移覆盖信用。实际两次生成、规则套件、原生/托管两配置、1358、私有14包和干净消费者继续待验。

F5首入口把不含proposedVersion的准备文件当作版本来源，拒绝发生在构建和推理前；修订从来源哈希已冻结的Directory.Build.props读取已测试SDK27。第二入口遇到另一任务实际Debug原生测试，保留原拒绝并等待自然结束，没有终止任何进程。当前[实际供应商构建](../../artifacts/f5-inspection-role-feedback-draft-1/provider-build-1-verification.json)通过，原三份C#工作流源码、模型/endpoint、8轮/16工具轮/8192上下文/6000及2400结果预算和断言保持。工程图已启动并捕获实际62；工作流终态尚未取得，不能记PASS。

SDK29恢复[新1059来源](../../artifacts/af-sdk29-recovery-role-draft-2/preparation-1.json)只更新私有feed路径，八份恢复4源码和原6448测试不变。实际nativeSha256仍为空，只有当前生成/原生/运行/包/公共整图通过后才能填入真实值并刷新冻结锁。包队列明确等待原供应商和SDK运行两类依赖，避免供应商先结束就启动尚无运行证明的打包。新来源守卫和队列语法通过，语义编译/故障矩阵未通过。

生产1000/SDK26及正式A4/B17/C5/D5/E4/F5g保持；A–F ACTIVE，G暂缓。当前键鼠未占用，桌面验收前提示。

## status195 原模型跨文档通过与续页导航边界

[当前跨文档](../../artifacts/f5-inspection-role-feedback-draft-1/lmstudio-workspace-1-verification.json)在原Bonsai模型、8轮工作流、16工具轮、8192上下文和原提示/断言下实际通过，共29调用，保存/两个生产文件重开、真实HTTP与严格状态身份核对，关闭后资产0。该证据属于当前1057/SDK27，不覆盖工程图或SDK29 WPF恢复。

[当前工程图FAIL](../../artifacts/f5-inspection-role-feedback-draft-1/lmstudio-drawing-1-verification.json)停在第三轮：已完成两轮，共15工具调用，模型在成功native inspect_sources后空答，未执行要求的dimension create。实际原生45项包含第38项CylinderFace；HTTP压缩保留前20项，同时已经提供完整nextRequest和第38项的omittedGeometryReads提示。模型没有请求它。顶层hasMore=false属于原始查询的完整分页，不代表上下文没有省略。已有续页元数据必须与未被模型使用的事实分开，不能归因于原生没有返回或编造选择。

新[导航1058草稿](../../artifacts/f5-native-read-navigation-draft-1/preparation-1.json)保留原6448测试源码，只调整AgentToolResultCompactor：上下文导航放在长items前，contextPage.hasMore与queryHasMore分开；实际查询身份、顺序、完整项、原顶层分页、6000/2400字符预算保持。三项新回归直接使用本次成功原生收据和实际查询；没有自动查询重放、没有猜测身份。两份C#语法已通过，语义构建、相关测试、完整6451和原模型再验均未通过。串行语义入口等待当前SDK29/恢复队列结束，避免抢占原生消费者。

SDK29新draft2的Release、Debug生成器实际构建零警告/错误，各11专项和原207加11=218完整通过；两次生成及其独立审核未完成，原生/托管构建、各1358、包/公开消费者/WPF恢复继续待验。首CA1861、版本字段拒绝、外部占用拒绝及旧打包依赖队列拒绝保持原始文件；当前正确队列等待实际依赖，未改写失败为通过。

A–F ACTIVE，G暂缓，生产1000/SDK26及正式子批保持。键鼠未占用，下一次桌面输入开始前提示。
