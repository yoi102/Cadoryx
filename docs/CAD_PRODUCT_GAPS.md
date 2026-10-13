# 3D CAD 产品缺口与实施规划

2026-10-14，status255：D7第三版保留完整144新增/99原有及全部公差，[两配置实际243](../capability-evidence/diagnostics/d7-analytic-branch/revision3-verification.json)各207通过/36失败，原99及此前Far覆盖36均通过；剩36同名孔/岛起始材料偏差保持失败，两配置终态已审计，候选未合入。原生线框完整材料helper两配置已编译，但等待SDK标准生成PlanarWireFaces.Create的runtime/精确本地NuGet后才接评估，不窄化原曲线范围、不将托管编译当能力完成。SDK133源码资格已交回；第三版现已结束，根再次实际空闲核查后明确交还约30分钟，优先SDK完整面入口及精确包资格；尚无新包接收。生产1082/公开85、原24STEP精确延期及A–F全部范围保持，G/清理暂缓；正式已验子批仍A4/B17/C5/D5/E4/F5g。当前以[STATUS](STATUS.md)和实际终态为准。

2026-10-14，status254：[工程图旧入口路径反馈1082](AF_DRAWING_LEGACY_VIEW_PATH.md)已合入，两配置各49/49，原正常原生创建保留、实际native62匹配公开85，Tests及WPF build0；当前预计6847全量/真实模型未重跑。原1081/86完整6830各6806通过/原24精确延期失败保持。[原模型实际终态审计](../capability-evidence/diagnostics/af1081-local86/provider-verification.json)为工程图1/8、工作区5/8均失败，原预算/来源/native保持；旧预启动hold已结束。D7隔离候选两配置修订1各168/243通过、修订2各171/243通过；原99均通过，但72失败仍待修正，两修订均未合入，不代表D完成。正式已验子批仍A4/B17/C5/D5/E4/F5g；恢复、窗口、材质、性能及其他A–F继续，第4、5节范围保持，G/清理暂缓。当前以[STATUS](STATUS.md)和实际终态为准，以下均为历史。

2026-10-14，status253：[当前1081/本地86两配置完整6830](../capability-evidence/diagnostics/af1081-local86/full-verification.json)各6806通过/原24延期失败/零跳过，无新增失败，原名称及native62准确；Debug已补齐终态。[Far90两配置修订6](../capability-evidence/diagnostics/af1081-local86/far90-runtime-6-verification.json)各72通过/18失败，所有原始source/stop BRep字节保持；18为原生raw-full-near体积错误，完整Near/Far私有裁剪及孔岛/负例有各自通过记录，不代表Far产品完成。模型预启动协调25052实际hold，根已明确交还SDK CX25/CY8约30分钟资格时段，模型、恢复、窗口和性能仍待验；源版本/原第4、5节A–F范围保持，G/清理暂缓。最新以[STATUS](STATUS.md)和实际终态为准，以下均为历史。

2026-10-14，status251：用户允许[精确24项原有严格STEP失败延期修复](../capability-evidence/diagnostics/af1081-local86/step24-user-deferral.json)，继续其余A–F；它们仍是已知失败，不能记作通过或延伸为其他交换问题的豁免。Release6830已核对6806通过/原24失败/零跳过。原Debug进程已消失且缺终态，SDK明确交回时段、实际空闲核查后只重跑冻结Debug，输出`full6830-debug-2`；尚未完整验收。恢复、模型、性能、D6/材质实际窗口与完整90项孔岛Far前提仍待验；SDK最新55声明仅开发验证。生产1081/公开85、本地86仍隔离候选；第4、5节A–F范围保持，G/清理暂缓。当前以[STATUS](STATUS.md)及实际终态为准，以下记录保留历史。

2026-10-13，status250：[1081/本地86 Release完整6830](../capability-evidence/diagnostics/af1081-local86/release-full-verification.json)实际6806通过/原24失败/零跳过，原名称与新178及native62准确、无新增失败；Debug串行运行中。原24/36恢复工具已持久化并两配置publish0，实际恢复/整图审阅、原模型/性能/36 D6窗口未运行；模型队列在两配置结束后等待SDK验证时段明确交回。生产仍公开85、本地86仅候选。A–F全部范围/STEP24/AppSweep保持，G/清理暂缓。

2026-10-13，status249：[1081/本地86隔离验收](AF_SOURCE1081_LOCAL86_ACCEPTANCE.md)已完成50项目锁定恢复、两配置build0和各实际发现6830；SDK13/native62与共享provider16核对准确。Release完整回归实际运行，Debug待串行，模型队列等待两配置终态；原性能与36真实窗口尚未运行。生产仍公开85，本地86只在隔离候选。现有SDK会话继续生成器迁移并按需优先API；A–F原范围/STEP24/AppSweep保持，G/清理暂缓。

2026-10-13，status248：[1081主窗口失败回滚](AF_MAIN_WINDOW_ATTACHMENT_RECOVERY.md)已合入开发，两配置各223托管断言、Tests依赖图/WPF build0；当前预计6830尚未完整发现/运行，旧1080冻结不可替代当前验收。[本地86接收](../capability-evidence/diagnostics/local-sdk86-received/verification.json)2050来源/25交付文件及14包内容已独立核对，SDK明确交还时段且接收时消费者空闲；86未应用、生产仍公开85。原STEP24/模型/性能/窗口及A–F全部范围保持，G/清理暂缓；下一步构造1081/选定SDK统一最终载荷。

2026-10-13，status247：[GuidedSweep/AppSweep调用范围](AF_GUIDED_SWEEP_APPSWEEP_CALL_CHAIN.md)已核对13份来源，区分PipeShell/GeomFill_Sweep与实际AppSweep调用者；仅静态直接调用审查，未证明整体算法安全或完成修复。上游冷源首轮2253/2255、CC/CB两项窗口输入失败，完整复验和本地86包资格继续、时段尚未交还。开发仍1080/公开85，原最终6815、模型/性能/窗口未运行，原STEP24和A–F全部范围继续；G/清理暂缓。当前结论以[STATUS](STATUS.md)和1080验收记录为准，以下保留历史。

2026-10-13，status246：[1080最终验收准备](../capability-evidence/diagnostics/af1080-final-ready/verification.json)完成：1192来源保持，完整solution Release/Debug build0，实际最终WPF各208编译断言通过，provider16共享逐字节一致，SDK13/native62磁盘匹配公开85。76实际编译程序集的221类型/696成员集合与原1073逐项相同、零直接native imports；元数据不算能力/运行验收。原模型三份来源、6815全量、公差/预算、两fresh各20周期/6482ms/1800秒、生产Open900秒及完整788×411 PNG保持，实际均NOT RUN。[D6最新宿主](../capability-evidence/diagnostics/af1080-window-observer/verification.json)两配置build0、19生产/worker/SDK13/native62及资源一致，原36矩阵/29断言/六状态/30和300秒不变，桌面未开始。SDK仍占用native lane，包86待交付；不先启动85全量再重复86。[AppSweep归档诊断接收](../capability-evidence/diagnostics/appsweep-upstream/verification.json)五哈希及18源码匹配，归档两配置记录Z0/期望Z3；367原辅助输入匹配、172变化/16缺失，旧二进制完整来源未重认证，probe退出0表示复现缺陷，不能算修复或应用受影响证明。A–F全部范围及原STEP24继续，G/清理暂缓。

当前进度以status246为准；以下status245及更早保留历史。

2026-10-13，status245：[终端工程图收据1080](../capability-evidence/diagnostics/af1080-drawing-terminal-result/verification.json)已合入：终端CadCommandResult.Success采用生产JSON收据success，拒绝类型/inspection不再包装成成功；保留原消息、身份/游标与事务。新50项旧1079每配置30通过/20复现成功状态错误；候选和合入后根各208/208（原158+新50）托管断言直接调用通过，Tests依赖图/WPF两配置build0、SDK13/native62磁盘匹配公开85，无原生加载。当前源码1080/公开85；预计6815尚未完整发现/运行，原模型及最终性能/窗口未验，原STEP24未修复。SDK报告Release2255/2255，精确迁移594 Manual→Emitted加4 Blocked→Emitted，Debug/实际原生/包消费者继续、证据尚待独立接收；Cadoryx保持原生空闲。A–F全部范围继续，G/清理暂缓。

当前结论以status245为准；以下status244及更早是保留历史。

2026-10-13，status244：[工程图对象类型1079](../capability-evidence/diagnostics/af1079-drawing-object-type/verification.json)已合入开发：create/edit/reselect/delete在原文档/状态、只读及字段守卫后先拒绝错误type，返回该动作真实可用类型；feature/body/part不是图纸对象，reselect仅允许原有六类附着对象。没有选择owner/对象或填入身份，终端与AI共用生产反馈，原inspect类型集合/schema/12工具及8192预算保持。新101项旧1078程序集每配置30通过/71复现失败；最终候选和根每配置158/158（原57+新101）通过，Tests依赖图/WPF两配置build0、SDK13/native62磁盘一致，无原生加载。首轮新夹具缺ITechnicalDrawingKernel导致每配置33失败，真实回执已保存；补必需能力接口后仍禁止投影/几何，未放宽断言。当前1079/公开85；预计6765完整发现/运行、原模型及最终性能/窗口待验，原STEP24未修复。SDK仍持有原生时段继续整体迁移和运行时验证；A–F原范围保持，G/清理暂缓。

当前结论以status244为准；以下status243及更早保留为历史。

2026-10-13，status243：[命令目录1078](../capability-evidence/diagnostics/af1078-command-catalog/verification.json)已合入开发：cad_status.commands只列cad_command实际可接受的入口，HISTORY/WORKSPACE/WS独立标明专用工具，使用同一映射保留原拒绝规则；终端注册/HELP、身份/游标守卫、12工具及schema未放宽。原生产程序集新12断言每配置8通过/4复现失败，候选及合入后根每配置57/57（原45加新12）通过；Tests依赖图及WPF两配置build0，SDK13/native62磁盘匹配公开85，没有加载原生模块。直接调用编译断言不是完整xunit；预计6664尚未全量发现/运行，真实模型未复验，原24 STEP失败不算修复。当前开发1078/公开85，1077说明候选未采用；1076两配置6652终态继续作为历史全量证据。SDK整体迁移原生验证继续，Cadoryx性能/D6/材质窗口待交还且须绑定最新最终载荷；A–F全部范围保持，G与清理暂缓。

当前结论以status243为准；以下status242及更早是历史检查点。

2026-10-13，status242：[生产根两配置完整6652](../capability-evidence/diagnostics/af1076-root-full/verification.json)均已终态，各6628通过/原24严格STEP失败/零跳过，无新增失败，原6619名称、新33及实际native62保持。Release未重跑，只接续此前尚未启动的Debug；原PID2348拦截时身份未完整记录，不推断原因。[1077最终provider2实际运行审计](../capability-evidence/diagnostics/af1077-provider2/verification.json)确认工程图完成1/8轮、工作区4/8轮，两场景FAIL且资产0，实际native62与冻结载荷准确；工作区最后三个HTTP请求完整保留当前expected四字段及原恢复反馈，排除这些请求的数据裁剪假设，根因尚未证明。1077不合入，生产仍1076/公开Preview.85。SDK已接管原生时段继续整体迁移，准确增量588 Manual StableId加4 Blocked StableId，调用点/410输出/28612声明分开统计，原生/包验证尚未交付；Cadoryx先做托管定位，性能/D6/材质窗口待交还。A–F全部范围继续，G与清理暂缓。

当前结论以status242为准；以下status241及更早是保留的历史检查点。

2026-10-13，status241：开发生产源码为1076/公开Preview.85。[根Release6652](../capability-evidence/diagnostics/af1076-root-full/release-verification.json)实际6628通过/原24严格STEP失败/零跳过、无新增失败及native62准确，Debug仅续跑缺失配置、仍待终态。1077最终WPF两配置build0、实际应用原编译断言各45通过，探针共享16同哈希；原模型队列等待Debug结束。原6482ms/两fresh各20双视口周期、生产打开900秒及完整788×411 PNG验收程序已绑定最终载荷，性能尚未运行。原1076多文档8轮PASS，工程图第三轮FAIL，完整柱面数据可用不等于制造工作流完成。A–F全部范围及SDK CJ–CQ整体迁移继续，G/清理暂缓；当前结论以[STATUS](STATUS.md)为准。

2026-10-13，status238：A–F继续，尚未全部完成。[完整1076组合源码已合入开发目录](AF_SOURCE1076_DEVELOPMENT_INTEGRATION.md)，55现有/44新文件、1189输入匹配；生产根两配置编译0并实际发现6652。候选两配置各455专项通过、实际native62匹配，原6619名称保持；完整6652、合入后根原生与原模型复验尚未执行。历史检查已修复显式cursor:null与过期只读重查；原STEP24严格拒绝、真实D6窗口/材质/性能及第4、5节其余范围继续。开发阶段确需新封装/重编译时允许本地精确版本OcctSharp NuGet，接近收尾再统一公开包；当前仍公开85，Cadoryx不用vcpkg。G及清理暂缓。

当前进度以status241及[STATUS](STATUS.md)为准；status238及以下为保留的历史检查点。

2026-10-13，status235：A–F继续，尚未全部完成。[最终1073/public85 Release完整6619](../capability-evidence/diagnostics/public-nuget-migration/public85-final1073-release-verification.json)实际6595通过/24失败/零跳过，原用例与实际native62核验准确；原AI恢复预算失败已通过，原严格STEP24仍待修复，Debug正在串行运行，最终1073未合入生产。[F5默认分页配对](F5_DEFAULT_PAGE_QUERY_MATCHING.md)已修复当前生产源码，两配置独立21/21，但不是原模型工作流或全部产品验收。开发阶段需要新封装/重编译时可用本地精确版本OcctSharp NuGet，接近收尾再统一公开包；Cadoryx仍不用vcpkg。G与Cadoryx清理暂缓。

以下status234及更早为历史检查点，当前进度以status235和[STATUS](STATUS.md)为准。

2026-10-13，status234：[检查点6](../capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT6.json)核对Cadoryx公开迁移提交 `820e53f` 已推送到origin/master；OcctSharp实现/资格 `587cadd` 及清理收尾 `3e1f49a` 已推送，远端HEAD一致、SDK工作树干净。SDK清理117个可重建缓存目标、37,663,196,524逻辑字节（35.08GiB），最终包/运行时/许可/来源/证据保留；Cadoryx未清理。SDK优先时段已结束，最终source1073/public85的Release完整6619正在运行、实际native62来源/哈希准确，随后Debug6619；独立审计已排队，要求保留原6556名称及新增63、原断言/公差/预算、两配置完整TRX和实际加载载荷。已复现部分原严格STEP导出拒绝；本轮全量尚无终态，不算通过。A–F继续，G暂缓。

以下status233及更早记录保留当时状态；当前进度以status234为准。

2026-10-13，status233：[检查点5](../capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT5.json)确认公开 Preview.85 已实际应用生产，仅nuget.org、稳定SDK10.0.401，生产根50项目/120阶段/Release及Debug共60次编译均零警告/错误；30份SDK锁/412引用和六个输出的13托管/62原生DLL匹配公开包。实际公开能力探针55/55通过。vcpkg独立profile两配置各完整2255/2255、零失败/跳过，磁盘84DLL/22许可证；Release实际观察84模块、Debug实际观察40模块及桥的路径/哈希准确，Debug不证明全部84加载。最终source1073公开85完整6619两配置回归已开始，原严格STEP24、工具预算及实际窗口/性能/提供方门禁仍保持，最终1073尚未合入生产。Cadoryx不需要vcpkg；Native.win-x64继续受支持、不弃用。A–F继续，G及Cadoryx清理暂缓。

以下status232及更早记录保留当时状态；当前结论以status233为准。

2026-10-13，status232：公开 `8.0.1-preview.85` 完整14包已发布并完成签名/远端身份核验；Cadoryx 两组隔离消费者各50项目、120恢复/编译阶段、60次两配置编译通过，最终1073的221类型/696成员集合保持，当前生产为207/661。已将生产41份配置/锁及baseline切换到公开85，正式30份SDK锁/412引用通过核验；正在生产根用全新缓存完成恢复和两配置编译，原生运行尚未开始。[生产应用收据](../capability-evidence/diagnostics/public-nuget-migration/public85-production-applied.json)与[生产消费锁](../capability-evidence/diagnostics/public-nuget-migration/public85-production-locks-verification.json)已保存。Cadoryx 不用vcpkg；`OcctSharp.Native.win-x64` 继续提供原生运行时，无替代包或弃用计划，不标记过时。vcpkg第二profile的Release完整2255通过已由上游报告，Debug实际原生验证正在进行，独立证据待接收。A–F继续，最终全量/窗口/性能/提供方和原严格STEP24/AI预算失败仍待收敛；G及Cadoryx清理暂缓。

以下status231及更早记录保留当时状态；当前结论以status232为准。

2026-10-12，status226：用户要求先完成OcctSharp必要API的生成器封装/迁移并公开发布NuGet，随后Cadoryx切换公开包、提交推送；[前置计划及验收](OCCT_PUBLIC_NUGET_MIGRATION.md)保持第4、5节和G01–G24全部范围。上游会话准备85/ABI1.118/bridge0.133.0，尚未公开发布或生产接入；静态221类型/696成员引用不等于能力完成。SDK30两配置完整6530均通过，当前solver21 Release继续运行，后续等待上游时段。G及Cadoryx清理暂缓；用户新授权的OcctSharp编译缓存清理和`.gitignore`纳入上游任务。

2026-10-12，status225增量：[D6/SDK30 Release完整6530/6530](../capability-evidence/AF_PENDING_D6_SDK30_INTEGRATION_FULL_RELEASE.json)已通过，零失败/跳过、实际native62与冻结来源/原名称再次核验；Debug全量运行中。生产接入、真实窗口及其余A–F门禁仍待验，第4、5节和G01–G24范围保持，G及清理暂缓。

2026-10-12，status225：**A–F继续，G和清理暂缓，尚未全部完成。** [D6/SDK30生产组合](D6_SDK30_PRODUCTION_INTEGRATION.md)source1066两配置构建0警告/错误，发现6530，各有限曲面99/99与原AI/工程图165/165及实际native62通过；完整6530正在验证，随后续solver21原6556与最终D6的6619两配置。修订5窗口观察器已修正模态调用顺序并编译，36真实创建/编辑场景仍NOT RUN。[原source1070提供方](../capability-evidence/AF_PENDING_F5_SOURCE1070_FULL_PROVIDER_VERIFICATION.json)跨文档8轮通过、工程图失败；完整6556各6432/124，不能当最终1073门禁通过。[精确插结点14组](B18_EXACT_KNOT_SUBDIVISION_DIAGNOSTIC.md)全部拒绝，严格STEP和外部完整几何等价仍待解决。生产仍1000/SDK26，正式已验仍A4/B17/C5/D5/E4/F5g；[检查点](../capability-evidence/AF_PENDING_STATUS225_CONTINUATION_CHECKPOINT.json)记录真实范围，不缩减第4、5节与G01–G24。以下status224及更早为历史。

2026-10-12，status224：**A–F继续，G及清理暂缓。** [D6新实体与自由起始平面](D6_FINITE_SURFACE_NEW_BODY.md)隔离source1073 / SDK31已实现，两配置构建0、原6556+新63发现6619，最终各有限曲面99/99及AI/工程图189/189、实际native62/完整1073恢复重放通过。完整6619已排队，实际窗口、性能、提供方及生产接入待验；生产仍source1000/SDK26，正式已验子批仍A4/B17/C5/D5/E4/F5g。原严格STEP24拒绝及其余第4、5节范围保持；以下status223及更早是历史检查点。

2026-10-12，status223：**A–F 继续推进，尚未全部完成；G 实机、安装及交付暂缓，清理保持停止。** 隔离source1070 / SDK31 / solver21收紧完整作者处方守卫；两配置完整solution构建零警告/错误，原6556来源/名称保持。原986功能专项两配置各962通过/24失败，原99精度失败全部消除、原306耦合正例全部通过；F5路径及上下文两配置各29/29。剩余24仍为原精确STEP导出拒绝，守卫、公差、迭代预算保持。两文件字节补丁已实际重放全部1070；完整6556两配置已排队，当前未运行，生产仍source1000 / SDK26。原全量和Bonsai队列保持，不以旧载荷或本轮功能诊断代替最终窗口/材质/性能及提供方验收。详情见[完整作者守卫记录](B18_COMPLETE_AUTHOR_SOLVER_GUARD.md)与[独立功能审计](../capability-evidence/AF_PENDING_SOLVER21_COMPLETE_AUTHOR_FUNCTIONAL.json)。

原status209及更早记录以下保留当时状态；当前证据以[STATUS](STATUS.md)和[A–F跟踪](AF_IMPLEMENTATION_TRACKER.md)为准，第4、5节及G01–G24范围保持。

当前[Release完整6467/6467](../artifacts/af-sdk30-history-identity-draft-5/full6467-release-1-verification.json)与[Debug完整6467/6467](../artifacts/af-sdk30-history-identity-draft-5/full6467-debug-1-verification.json)均独立通过，零失败/跳过、原用例名称及实际SDK30/native62保持。[原Bonsai两个实际结果](../artifacts/af-sdk30-history-identity-draft-5/provider-both-1-exit.json)均失败：工程图在创建阶段缺少成功收据，跨文档第八轮超原16工具轮预算；不修改提示、身份守卫或预算。当前1066的相关980失败后，[最终应用准备](../artifacts/b18-single-loop-mixed-joints-draft-2/final-app-after-baseline-1-exit.json)按原门禁停止，未启动1066发布或桌面。已发布1063只作基线，不能代替后续修正候选的最终验收；原24和新36须绑定最终载荷后重编译/实际运行。

当前[原WPF24及主壳状态栏36验收程序](A5_MAIN_STATUS_RECOVERY_ACCEPTANCE.md)均已编译、引用载荷已验，运行尚未开始。原24的C#逐字节保持、48保存/52整图及30秒预算不变；新36通过真实生产MainWindow、实际原生失效/绘制消息核对三种语言和两个活动pane的状态绑定、文档历史与144整图。首轮空引用编译失败保留，未调用私有恢复或写状态文本；原上下文/GPU故障仍未验。

[B18重基1066](../artifacts/b18-single-loop-mixed-joints-draft-2/pipeline-after-provider-1-exit.json)两配置构建零警告/错误、发现原6467加59的6526，各17托管已独立通过。原Release相关980实际为953通过/27失败：42混合原生案例中有求解不收敛、非圆/直线偏移导出拒绝和独立源面积精度超限；原日志/TRX保留。Debug相关980及两配置完整6526未运行，未证明全部42或B18完成。两个窗口辅助程序及原窗口/性能条件保持；未控制键鼠。下一步先诊断修复，保留原矩阵、公差和256变量/512方程/80迭代，再验完整门禁。

以下G01–G24及第4、5节保持原范围；较早运行状态为历史，不能替代当前门禁。

## 1. 当前处于什么位置

Cadoryx 已建立参数化零件、装配实例、精确 BRep、关联重算和工程交付的基础链路，可完成限定范围的建模、审阅和图纸输出。目前仍处于功能扩展与产品稳定性收敛阶段，尚不适合按成熟通用机械 CAD 的完整能力进行承诺。

最需要推进的两条线是：

1. **把已有操作做稳定、可重复验收**：真实键鼠与输入法、预览提交、选择与引用、保存恢复、大模型等待，以及设备环境。
2. **扩展已有工具的覆盖范围**：继续收敛草图复杂关系、曲线偏移/投影、任意曲面、引用历史、制造规则与外部交换；已经完成的限定契约见第 2 节与 A–F 跟踪。

M14–M17 已记录的待办主要关注性能、精确建模边界、工程交付与环境。它们还没有覆盖上述全部机械 CAD 工具。本文将额外工具列为**产品建议待办**；用户已授权完成第 4 节 A–F，G 第七批延期；当前恢复 A–F；实施任务和未验收门禁见 [A–F 总实施跟踪](AF_IMPLEMENTATION_TRACKER.md)。B1–B7 是 B 大批的子任务，并不代表七个产品批次均完成。

## 2. 已有能力及准确边界

| 领域 | 当前已有 | 使用边界与证据 |
|---|---|---|
| 文档与应用外壳 | 多文档、Ribbon、停靠工具箱、模型树、属性、三语言资源、文档命令和撤销/重做 | 保存、恢复和迁移已有本机验证；真实介质故障及新机器仍待验收。[当前进度](STATUS.md) |
| 基础实体建模 | Box、Cylinder、高级拉伸、曲线/孔岛旋转、布尔、镜像/阵列、常用孔和实体螺纹、扫掠/放样、抽壳/拔模及整体实体偏移 | C1–C5、D1–D5、B8/B9分别有本地契约证据；任意曲面终止、自由起始面等剩余范围按G07/G10–G13跟踪。[配方](../Cadoryx.Db/Features.cs) |
| 局部建模 | 精确边圆角/倒角、有序切线链、多轮廓五种半径规律、多支撑双距离倒角、有限平面推拉和删除面修补 | [E3](E3_CONTOUR_LOCAL_EDITING.md)及[E4](E4_PLANAR_FACE_EDITING.md)的本地契约已验；修订改变后按规则冻结和重选，任意曲面移动/替换及缺失原生历史继续 |
| 草图 | 点、直线、矩形、圆、圆弧、Bézier/作者样条；尺寸及端点/内部G1/G2关系、修剪/延伸、投影、解析闭环/孔岛的Miter/Round/Bevel偏移 | [B9](B9_EXACT_CURVES_AND_EDITING.md)的4–1024控制点和非均匀结点、[B16](B16_SLIDING_CONTACT_PARAMETERS_PLAN.md)的四种滑动接触求解、[B17](B17_ANALYTICAL_OFFSET_JOINS_PLAN.md)的解析偏移连接分别已验。关系预算256变量/512方程保持；自由曲线精确偏移和更复杂诊断继续 |
| 区域与关联 | 精确混合边界、曲线孔和递归岛、自由工作平面、关联平面/轴/点、草图驱动后继重算 | B9 精确区域检查及 C2 命名/精确来源基准已实现；预算、冻结/重选和依赖边界分别见 [B9](B9_EXACT_CURVES_AND_EDITING.md) 与 [C2](C2_EXTENDED_DATUMS.md)，不自动猜测失效拓扑 |
| 特征历史 | 依赖重算、失效诊断、参数编辑、抑制/恢复、临时回滚游标、明确分支插入和合法历史重排 | [E1](E1_FEATURE_TIMELINE.md)显示精确缓存中间输出，插入保留原后继图；任意配置和自动分支重放仍另立契约。旧E1一般窗口失败保留为历史，后续同版窗口通过见[当前状态](STATUS.md) |
| 视口操作 | 精确拾取、标准视角、ViewCube、剖切、双视口、尺寸手柄、实例移动/旋转/受限缩放、圆盘菜单 | 缩放操纵器只对唯一使用的 Box/Cylinder 特征修改尺寸；真实滚轮修复仍需设备复验，复杂面编辑未形成通用闭环。[M12](M12_INTERACTION_AND_PERFORMANCE.md)、[圆盘菜单](RADIAL_MENU.md) |
| 装配 | 定义/多层实例、固定/点重合/距离/平行轴/同轴/角度/平面贴合关系、真实解析基准、闭环局部求解、自由度与冗余诊断 | 求解最多涉及 32 个实例、128 个启用关系，迭代预算 5 秒；局部收敛不证明全局唯一。外部零件按冻结快照显式刷新。[M10](M10_ASSEMBLY.md) |
| 检查与审阅 | 精确几何检查、测量、剖切、关联截面/封口、干涉与接触检查、隐藏/隔离、书签及标注 | 已有静态检查；动态机构运动和运动过程碰撞尚未列入当前应用交付能力。[M6 收尾](M6_COMPLETION.md) |
| 工程图 | 多张纸和六类视图、精确剖面线、制造图框/标题栏、模板库、关联尺寸/基准/GD&T框、装配气泡/零件表/孔表、矢量PDF与打印入口 | [F1](F1_EXACT_SECTION_HATCHING.md)、[F2](F2_MANUFACTURING_TEMPLATES.md)、[F3a](F3A_ASSOCIATED_ANNOTATIONS.md)至[F3h](F3H_PLANAR_SIZE_FEATURES.md)及[F5g](F5_COMPLETE_DRAWING_TOOLS.md)分别有本地证据；进一步制造规则、缺失算法历史、完整标准合规和实纸打印仍待完成 |
| 交换与工程交付 | .cadoryx、STEP、IGES、STL；BOM、HTML审阅、交付ZIP及实际重读的交换审计报告 | [F4](F4_MEASURED_EXCHANGE.md)已有真实FreeCAD几何往返证据；外部层级、共享定义、逐面色、PMI及完整几何/元数据等价仍分别验收，不能以面积/边界一致代替 |
| 命令与 AI | 终端和结构化工具覆盖参数、基准、建模作者、历史、文件、多文档及既有工程图作者入口；LM Studio/Codex接入 | [F5g](F5_COMPLETE_DRAWING_TOOLS.md)及各F5子批确定性入口已有本地证据；Codex会话通过与LM Studio仍失败的场景分开记录，提供方长期稳定性继续验收 |
| 安装 | 自包含 Windows x64 MSI、本机安装目录启动、卸载及用户数据保留 | 已记录的 MSI 是此前版本的本地测试包，不能代表当前最新源代码；测试证书不受 Windows 默认信任。[M17](M17_RELEASE_MATRIX.md) |

当前工作依赖以[Directory.Build.props](../Directory.Build.props)、项目锁文件及[runtime-baseline](../Cadoryx.wpf/runtime-baseline.json)为准：**公开8.0.1-preview.85**，ABI1.118/桥0.133.0；Cadoryx仅消费NuGet、不需要vcpkg。当前开发源码1080。1076两配置完整6652各6628通过/原24严格STEP失败；1078/1079/1080新增命令目录、类型反馈及终端成功状态的当前托管证据见页首，预计6815尚未完整运行。历史1000/SDK26及隔离性能不能代替当前载荷验收。

## 3. 具体还缺什么

状态含义：**未实现**指未在当前应用配方、命令和用户入口中形成可用工作流；**受限**指已有能力但覆盖范围不足；**未验收**指缺少相应场景的实际证据。未实现的应用工具不等于 OCCT 或 OcctSharp 没有底层 API。

优先级是本报告建议：P0 为已有操作的正确性和主要可用性问题，P1 为普通机械建模及交付的高频缺口，P2 为目标用户明确后再扩展的专业能力或研究性项目。

| ID | 能力/问题 | 当前状态 | 用户影响与下一步 | 优先级/归属 |
|---|---|---|---|---|
| G01 | 真实交互稳定性 | 原圆盘与综合窗口已验，剩余真实输入待验 | 已有前台静止像素换页、随后移鼠不改页及窗口循环证据；当前picker/G15、实体创建/取消、IME、不同DPI和真实点编辑仍按确切构建验收 | P0，已有交互收敛 |
| G02 | 大模型打开与完整显示 | 当前组合未实测；历史隔离候选重复达标 | 历史1000第二轮6800.2534ms超原6482ms；[隔离1056两fresh进程](../artifacts/af-merged-performance-verification-2.json)完整提交4.752/5.061秒、各20双视口周期及原788×411整图通过。[1077最终载荷](../capability-evidence/diagnostics/af1077-final-app/verification.json)及原严格验收正文已准备，尚未运行。导入Open与direct分列；历史隔离结果不代表当前生产达标 | P0，M14/A5进行中 |
| G03 | 草图修剪、延伸、曲线偏移、外部几何投影 | B1–B17限定契约已验；B18持续接入 | SDK22完整4498、曲线消费者80/关联104两配置及[当前24三语言窗口/24保存文件](../artifacts/b18-curved-window-verification-2.json)已验。来源ID/方向/端点、C1驱动、原子拒绝/撤销/保存恢复保持；迟发竞争、间接实时表达式、同环混合接头、一般交换及缺历史继续；分裂引用冻结并明确重选 | P1基础已有 / 后续范围扩展 |
| G04 | 更完整的草图约束 | B3–B5/B7/B12–B16限定范围已验；B18耦合关系持续验证 | 中点/对称、圆弧尺寸、端点及有限内部G1/G2、四模式自动求解/绑定/迁移已有证据；B18解析接触、关联端点与自然区域求解已接入。当前点耦合保留完整生成点变量和结构方程，检查全部残差/Jacobian；无证明接头、循环及实时几何依赖拒绝保持。其余关系作者、同环混合与失败诊断继续，不把局部失败判成全局无解 | P1基本关系 / P2扩展，M15 |
| G05 | 任意样条与精确曲线关系 | [B9](B9_EXACT_CURVES_AND_EDITING.md)本地契约完成 | 4–1024点作者样条、任意合法控制多边形、非均匀结点、精确有限交点/投影/区域检查及精确曲线编辑已做；1192/1192及同版十三门禁PASS。有启用关系仍遵守256变量/512方程预算；复杂关系与曲率连续继续 | P1曲线基础完成 / P2关系扩展，M15 |
| G06 | 旋转支持常用曲线/孔轮廓 | B8旋转与B9作者曲线本地范围已完成 | [B8](B8_CURVE_AND_REGION_REVOLVE.md)支持圆、直线/圆弧/Bézier/样条闭环、孔岛及关联轴，解析极值拒绝内部切轴/穿轴；[B9](B9_EXACT_CURVES_AND_EDITING.md)扩展合法非均匀作者样条。真实指针与外部CAD证据仍分列 | P1本地范围已验；整体B仍继续 |
| G07 | 拉伸/切除的终止条件和方向 | D1及D5限定契约已验；D6已合入开发、真实窗口待验 | [D1](D1_ADVANCED_EXTRUSION.md)正反/对称/双向、加料/去料、贯穿/斜平面及关联基准已有；[D5](D5_FINITE_SURFACE_TERMINATION.md)有限平面/球面/柱面、完整终止盖面覆盖和明确近/远分支已有。[D6](D6_FINITE_SURFACE_NEW_BODY.md)新实体及自由起始平面已随1076合入，原99专项与1076完整6652两配置已取证；原36真实窗口未通过。任意终止曲面、自由曲面起始及完整Far生成继续；源变化冻结重选 | P1，D6持续验收 |
| G08 | 用户参数、表达式、跨特征尺寸关联 | C1 已实现有界体系 | [C1](C1_PARAMETER_EXPRESSIONS.md)：命名/单位、表达式依赖/循环、设计值驱动/参考、草图及特征/图纸原子重算、文档 v21 和三语言窗口。当前限已适配设计字段，新特征字段与配置表随后扩展 | P1，已授权实施 |
| G09 | 关联基准面、基准轴、基准点 | [C2扩展](C2_EXTENDED_DATUMS.md)本地契约完成 | 精确BRep解析来源、轴点/相交构造、参数/阵列/孔/草图和冻结重选、原始资产及生产窗口已做；988基线同版门禁PASS。后续旋转轴、存储、三语言控件与失效/晚到/数值边界完成；最终1013/1013和同版联合门禁PASS（20261002-005501） | P1，已授权实施 |
| G10 | 实体/特征镜像与线性、圆周阵列 | C3完整作者图/特征重放/装配镜像阵列及多副本样式/原生历史本地契约完成，最终2296/2296及同冻结六窗口PASS | [C3多副本记录](C3_MULTICOPY_STYLES_HISTORY.md)：独立颜色通道/层/材料/可见性及融合冲突、容器原生成员证明/显式CopyId已验；STEP/FreeCAD元数据和额外拓扑损失继续跟踪F4 | P1，已授权实施 |
| G11 | 标准孔与螺纹表达 | C4 常用孔型及自动化控件范围完成 | [C4](C4_STANDARD_HOLES.md)统一通孔、盲孔、沉孔/沉头及常用尺寸、参数/基准/保存和生产编辑；[C5符号螺纹](C5_SYMBOLIC_METRIC_THREADS.md)304最终联合、六冻结窗口及16SDK通过；[实体牙型](C5_MODELED_METRIC_THREADS.md)真实牙槽/完整重放及proof、独立worker、六窗口、三语言同DLL图纸/3页矢量PDF及载荷审计已验，worker修后6/6与旋转通孔1/1；D5冻结2498全量及B15新schema51的55项螺纹联合已PASS；B15冻结2563全量PASS，B16第六冻结schema52集成2848全量PASS。A4长投影取消已验，真实输入/性能另列 | P1 孔 / P2 螺纹，产品建议待办 |
| G12 | 扫掠与放样 | 基础曲面/关联/取消/本地交换及SDK26完整当前出现路径已接入；完整历史材料跨作者范围继续 | 原80真实曲面、104关联/存储及640取消竞争有历史证据；[完整路径应用接入](B18_APP_OCCURRENCE_INTEGRATION_PLAN.md)已从source1000进入后续组合，当前1080/公开85保留。SDK23复制域/SDK24历史域/SDK25同图保存不能仅凭上游通过算应用全部能力完成；兼容/实体化/材料/第三复制及跨作者对应继续。导轨、扭转与更高阶连续性另列，不以旧窗口替代当前验收 | P1基础已有 / P2高级及完整原生身份继续 |
| G13 | 抽壳、拔模和实体偏移 | [D3抽壳/拔模](D3_SHELL_DRAFT.md)及[D4整体实体偏移](D4_SOLID_OFFSET.md)当前本地契约完成；D4 37专项/178联合/2065全量及同冻结六窗口PASS | 带符号完整源偏移/参数/失败/后继/工程图/STEP/存储已验，单实体包装严格提取；逐面色和原始拓扑不能证明继承，任意复杂目标与真实指针另列 | A–F授权内；不代表整个D或A–F完成 |
| G14 | 曲面建模、缝合及受控修复 | 未形成完整编辑工作流 | 可以处理/保存相应几何种类，不等于已有曲面工具；补有范围的建面、修剪、延伸、缝合和修复预览。ODA/Quadcept 无效样本保持诊断，修复应显式确认并报告变化 | P2；ODA 兼容诊断属 M15 |
| G15 | 通用多边局部特征与复杂面直接编辑 | 整链/多轮廓及有限平面编辑有本地契约 | [E3](E3_CONTOUR_LOCAL_EDITING.md)接入原生有序切线链、多轮廓五规律圆角和多支撑双距离倒角，包含参数、存储、UI/作者及后继；[E4](E4_PLANAR_FACE_EDITING.md)有限面域推拉/删除面修补完成本地契约：95专项/2211全量及七窗口；每个选面必须原生确认删除。任意曲面移动/替换仍需逐几何类型交付，不能推广受限尺寸手柄 | P1 基础 / P2 自由面，M15 扩展 |
| G16 | 历史回滚位置、重排及分支/配置 | [E1](E1_FEATURE_TIMELINE.md)游标/临时输出/分支插入/合法重排已实现并专项验证 | 36 专项、1206 全量、三语言生产历史窗口和 21 截图通过；其历史版本一般窗口圆盘定位失败，最新E2同版窗口通过。新增分支保留原后继，任意配置及自动分支重放另立契约 | P1 实现及专项完成 / P2 配置另立契约 |
| G17 | 跨重算拓扑引用延续 | 受限 | F3f原始面/边及[F3g](F3G_COMPOSED_DRILLING_HISTORY.md)组合钻孔原生重放与制造附着延续已验证；拆分、合并、多路径或新修订时仍可能要重选。[F3h](F3H_PLANAR_SIZE_FEATURES.md)新增两面各自作者核验及完整尺寸重验；E4平面Boolean和有完整删除集合/全源槽/计划/序列化成员证明的E5删除面历史已合入1076，语义入口也要求原生重放；未证明逐面样式仍不继承，旧最终边变源冻结后明确重选。缺历史算法继续扩展；通用自动命名列研究项，不能靠最近距离或遍历索引猜测 | P1 有界延续 / P2 通用命名，M15 |
| G18 | 大规模复杂装配与机构工作流 | E2有界契约已有历史1222/十五窗口；当前956性能修复定向24及相关103两配置通过，完整4172通过；当前窗口审阅继续 | [当前冗余诊断修复](../artifacts/b18-assembly-repair-checkpoint-verification-1.json)保留原列缩放、全局阈值、完整零空间及32实例/128关系/5秒预算；超预算或失败返回原文档，精确预览确认与原生基准重验。局部收敛不证明全局唯一；齿轮/滑块、机构运动另立范围 | P1 求解补强 / P2 机构另立契约 |
| G19 | 完整材料与渲染材质 | 材质持久化/恢复呈现已合入1076；同组合实际窗口待验 | 高级外观支持PBR、正反面、透明模式、单张PNG/JPEG纹理及映射；原82/346整图和picker/明确添加应用/分配/撤销重做/保存重开/三语言明暗为各自历史载荷证据，不代替当前1076/公开85验收。密度G15修复实窗和物理密度保持。完整工程属性、材质库及交换协议、自动图形故障恢复仍未完成 | P2，M16扩展 |
| G20 | 更完整的制造工程图 | 持续扩展/设备未验收 | F1剖面线、F2图框、F3a关联标注/GD&T/装配序号、F3b模板复用、F3c当前有效孔表已验证；F3d关联C2坐标/比例已验证；F3e圆柱尺寸/基准、F3f原始附着和F3g组合钻孔历史已验证；[F3h完整棱柱厚度/矩形贯通槽双面尺寸与中面](F3H_PLANAR_SIZE_FEATURES.md)亦已验证，drawings12/1683全量/八窗口；进一步规则与缺失历史继续。打印比例需实纸核验 | P1，M16 + 制造扩展 |
| G21 | 交换互操作与更多格式 | F4实测报告及FreeCAD几何往返已验证；完整元数据互操作未验收 | STEP/IGES/STL 要实测单位、层级、位姿、面色、PMI及损失；DXF/DWG 等二维交换未列入当前读写入口，按工程图用户需求选定首个格式 | P1 往返 / P2 新格式，M16 |
| G22 | 命令/AI的建模覆盖与稳定性 | F5a–F5g确定性入口已验；供应商稳定性未完成 | 参数、基准、建模、历史、文件、多文档及[工程图作者](F5_COMPLETE_DRAWING_TOOLS.md)保持精确状态/对象校验、取消与撤销。Codex通过不能替代LM Studio多文档/工程图失败；长会话、附件及外部服务继续按实际结果验收 | P1命令覆盖与供应商收敛 / P2扩展 |
| G23 | 实机环境和持续运行 | 未验收 | 干净 Windows、混合 DPI、RDP、多 GPU/驱动、数小时建模、真实介质故障和物理打印分别取证；VM 验收此前暂缓，不能计为已通过 | P1，M17 |
| G24 | 当前版本安装包、受信任签名及正式分发 | 旧本机测试包已有，正式交付未完成 | 为待交付源码重新构建并核验安装包，测试升级/回退/卸载及许可；受信任签名、时间戳、外部 CI 与最终公开下载各有独立证据 | P1 交付；实际发布按授权，M17 |

### 按目标用户选择的专业模块

当前应用工具目录中尚未形成以下完整工作流。它们适合单独立项，是否优先取决于产品定位：

| 方向 | 后续能力 | 建议定位 |
|---|---|---|
| 钣金与焊接 | 板厚/折弯特征、展开、折弯表、焊接结构及符号 | 目标是机箱/结构制造时提前；需材料和制造标准契约 |
| 自由曲面与 3D 草图 | 空间路径、曲面连续性、复杂导轨/多截面造型 | 建立在扫掠/放样及精确曲线体系之后 |
| 标准件与设计配置 | 紧固件库、零件系列、不同参数配置及配置 BOM | 先完成参数表达式、定义/实例和版本关系 |
| 专用基本体快捷工具 | 球、锥、圆环等专用创建入口 | 当前只有 Box/Cylinder 专用基础配方；可作为后续效率工具，部分造型可由受支持的旋转流程组合 |
| CAM/CAE | 刀路、后处理、有限元网格及求解结果 | 需要独立算法、外部集成和结果验证范围 |
| 团队数据管理 | 版本/审签、权限、PDM/云协作、共享零件库 | 当前本地文档与外部冻结链接基础之上的独立产品方向 |

## 4. 推荐怎么做下一步

2026-10-14执行顺序更新：原24项严格STEP失败按用户授权单独延期，不等待其修复即可继续其他批次。其他功能、实际窗口、模型、性能及交换门禁继续执行；延期不等于完成，也不降低原第5节完成标准。

当前正式已验子批保持 **A4 / B17 / C5 / D5 / E4 / F5g**。[B17解析偏移连接](B17_ANALYTICAL_OFFSET_JOINS_PLAN.md)已完成解析直线/圆弧/圆闭环与孔岛的限定本地契约；后续B18、A5及其余A–F仍有未完成项。生产源码1082已加入[工程图旧视图路径反馈](AF_DRAWING_LEGACY_VIEW_PATH.md)，仍使用公开Preview.85；两配置聚焦49/49及最终WPF build0，当前预计6847全量及模型重跑未执行。[1081/本地86完整6830两配置](../capability-evidence/diagnostics/af1081-local86/full-verification.json)已终态，各6806通过/原24精确延期失败/零跳过，无新增失败；不得替代当前1082完整验收。原模型两场景均失败、最新性能和窗口仍待验。D7候选尚未合入；全部第4、5节范围保持，G暂缓。

建议优先把“简单机械零件设计 → 装配 → 关联工程图 → 交换/安装交付”做成可靠闭环。下面是实施批次建议，不是新增阶段的完成声明，也不包含未经确定的工期。

| 批次 | 主要任务 | 交付结果 | 进入下一批次前的完成标准 |
|---|---|---|---|
| A：已有操作与等待 | G01、G02；继续 M14 | 键鼠/IME 回归清单，固定样本性能报告，数据与引用错误的明确反馈 | 真实操作能一致完成；取消/失败不留半提交结果；首画面和完整显示分段计时。性能达标结论须由同条件重复运行支持 |
| B：日常草图与旋转 | G03、G04 的基本关系、G06 | 修剪/延伸、首批偏移/投影、常用曲线轮廓旋转 | 改草图后实体、局部特征和图纸按规则更新；不合法轮廓明确拒绝；预览/取消、撤销和保存重开一致 |
| C：参数与重复设计 | G08、G09、G10、G11 的常用孔型 | 命名参数和关联基准基础；首批镜像、阵列和孔特征 | 修改一个驱动值后全部依赖原子重算；循环、过期基准、非法数量/间距拒绝；旧文件可迁移 |
| D：常用实体扩展 | G07、G12 的基础扫掠/放样、G13；按目标零件挑选 | 能覆盖支架、轴套、薄壁壳体和简单过渡/管状零件 | 每种特征至少有真实 BRep 正例、算法失败/自交反例、参数编辑、后继引用及持久化验证 |
| E：复杂建模与装配收敛 | G05、G15、G16 的回滚、G17、G18；继续 M15 | 有界样条/局部特征扩展、引用诊断、回滚及装配样本集 | 不以近似值写入精确模型；不将局部数值解报告为全局唯一；共享、多路径、奇异和失效均有可解释结果 |
| F：制造与互操作 | G20、G21、G22 的高频工作流；继续 M16 | 可用制造图纸、外部 CAD 往返报告、稳定命令入口 | 修改模型后投影与尺寸一致；过期内容不能误作有效输出；PDF/实纸比例正确；声明损失与实测损失分开记录 |
| G：版本交付 | G23、G24；继续 M17，可提前准备环境 | 当前构建的可核对安装包、环境矩阵与发行资料 | 无开发环境依赖的安装/首次使用/升级/卸载实测；固定包哈希、签名和设备日志；公开发布另需实际执行与结果 |

当前阶段记录中的 M14 场景 CPU 提交目标是相对 M12 的 9.26 秒基线降低至少 30%，约 6.48 秒。生产打开的端到端计时与这个直接基准不属同一路径，必须同时报告且不能混算优化率。若目标不可达，应记录瓶颈和实际收益，不能因已做计时或分批显示而宣布性能阶段完成。

**已批准的功能批次**：[B1](B1_SKETCH_EDIT_AND_REVOLVE.md)完成直线修剪/延伸与解析曲线旋转；[B2](B2_CURVE_EDIT_OFFSET_PROJECTION.md)增加解析曲线编辑、单实体偏移与草图关联投影；[B3](B3_LOOP_OFFSET_AND_BASIC_RELATIONS.md)增加单闭环解析偏移及中点/对称关系；[B4](B4_ENDPOINT_TANGENCY.md)增加直线—圆弧/圆弧—圆弧端点平滑相切；[B5](B5_PARAMETRIC_ENDPOINT_TANGENCY.md)扩展 Bézier/样条端点 G1 和样条首尾 ID 捕捉。[B6](B6_SKETCH_FILLET.md)已补指定半径的两条共享端点直线圆角，明确裁剪、约束清理及旧引用原子拒绝，当时半径为创建尺寸。[B7](B7_SKETCH_FINISHING.md)现已实现圆弧尺寸、倒角、多角应用、显式轮廓更新与三语言诊断五项；多环偏移及实体拓扑边投影仍需独立几何/身份契约。A 批实体滚轮/IME/鼠标建模人工回归及固定大模型硬件基准尚未完成。

如果目标用户主要做薄壁外壳，可将抽壳/拔模提前；如果主要做大型装配审阅，则将 M14、交换来源兼容性和装配规模放在新建模工具之前。该取舍需要按实际样本和用户任务确定。

## 5. 每个新 CAD 功能怎样算做完

1. **数据与几何**：有明确的参数、单位、输入/输出和引用语义；真实 BRep 构造和必要的精确检查成立。
2. **可操作**：界面能完成选择、参数输入、候选预览、确认和取消；错误信息能定位输入或引用问题。
3. **文档一致**：一次命令原子提交；撤销/重做、依赖重算和后继失效规则正确；失败保留原文档。
4. **可保存**：稳定 ID、资产/修订、格式版本和迁移明确；保存/重开及恢复不改变原语义。
5. **可输出**：按功能检查 STEP/IGES/STL 或图纸/PDF；记录格式不能表达的内容。
6. **有对应证据**：核心自动测试、桌面实际流程、相应输入设备或样本验收分别列出；需要设备/厂商的结论由真实环境给出。

对新工具，先通过锁定 OcctSharp 包的独立消费者确认底层能力。如果包已有所需算法，优先补 Cadoryx 的配方、命令、引用、存储和 UI。只有出现可复现的 API 缺口才扩上游封装，不能把整个上游迁移作为所有产品缺口的默认前置。

## 6. 需要专门补齐的证据

| 证据方向 | 当前可以引用什么 | 仍要取得什么 |
|---|---|---|
| 最新代码回归 | 生产1082/公开85，[旧视图路径反馈](AF_DRAWING_LEGACY_VIEW_PATH.md)两配置各49/49，Tests及最终WPF build0；此前[1081/本地86完整6830两配置](../capability-evidence/diagnostics/af1081-local86/full-verification.json)各6806通过/原24精确延期失败/零跳过，无新增失败、实际native62准确，已经完整终态 | 当前预计6847全量、模型重跑、性能及实际窗口仍待验；旧源码的完整回归不能替代当前验收。A5原24/36与D6窗口36运行断言保持，实际未运行；STEP24延期不豁免其他失败，第4、5节A–F全部范围继续，G暂缓 |
| 自动覆盖率 | [TEST_COVERAGE](TEST_COVERAGE.md) 记录 618 测试时生产程序集行 67.0%、分支 66.2% | 以新快照重测并补关键行为；WPF 界面和 OCCT 原生代码不在这些 xUnit 百分比内 |
| 性能 | [隔离1056两fresh进程各20双视口周期与生产Open](../artifacts/af-merged-performance-verification-2.json)通过，完整提交4.752/5.061秒满足6482ms；原完整788×411整图保持。生产缓存修复版6800.2534ms失败保留 | 当前候选生产接入、自动WPF/设备失效恢复待验；导入仍占主要时间，CPU提交计时不证明物理屏幕或输入延迟 |
| 厂商兼容 | [M15](M15_PRECISION_AND_ASSEMBLY.md) 的 CATIA V5、Open CASCADE、CoCreate、SolidWorks 读取/精确检查；ODA 样本被拒绝 | 不同来源的单位/层级/样式/PMI对照，以及外部 CAD 实际往返；为拒绝样本提供诊断或受控兼容方案 |
| 真实输入 | [RADIAL_MENU](RADIAL_MENU.md) 的排队滚轮与屏幕像素检查 | 实体鼠标、触控板、跨 IME、捕获/失焦、不同 DPI 现场操作；记录运行的是哪一构建 |
| 安装及环境 | [M17](M17_RELEASE_MATRIX.md) 的开发机 MSI 安装/卸载与自签名测试包 | 当前版本固定包、新机器、混合 DPI/RDP/GPU、长时建模、物理打印和真实介质实验 |
| 公开交付 | 本地许可与安装包资料 | 当前源码对应包、受信任签名/时间戳、外部 CI、发行说明和最终可下载版本的独立结果 |

不建议用“已完成百分比”概括整个 CAD：当前阶段有不少有界功能已实现，但缺口横跨产品工具、可靠性、几何契约和环境证据，没有同一可加总的分母。

## 7. 本次核对的主要源码与资料

| 核对对象 | 依据 | 用来确认什么 |
|---|---|---|
| 可用建模种类 | [Features.cs](../Cadoryx.Db/Features.cs)、[OcctGeometryKernel.cs](../Cadoryx.Kernel.Occt/OcctGeometryKernel.cs)、[CadDocumentViewModel.cs](../Cadoryx.ViewModels/CadDocumentViewModel.cs) | 实际配方、内核支持和创建/编辑入口；旋转与拉伸限制 |
| 草图工具与约束 | [Sketches.cs](../Cadoryx.Db/Sketches.cs)、[SketchEditorViewModel.cs](../Cadoryx.ViewModels/SketchEditorViewModel.cs)、[SketchSplineGeometry.cs](../Cadoryx.Db/SketchSplineGeometry.cs)、[SketchMixedProfile.cs](../Cadoryx.Db/SketchMixedProfile.cs) | 工具目录、约束类型、旧无结点样条16点/明确作者结点4–1024点的分别语义，以及256变量/512方程默认预算 |
| 历史与精确引用 | [RecomputeCommand.cs](../Cadoryx.Commands/RecomputeCommand.cs)、[FeatureSuppression.cs](../Cadoryx.Db/FeatureSuppression.cs)、[EXACT_LOCAL_TOPOLOGY](EXACT_LOCAL_TOPOLOGY.md) | 抑制实现、后继重算、单边精确绑定与重选边界 |
| 装配求解 | [AssemblyConstraints.cs](../Cadoryx.Db/AssemblyConstraints.cs)、[AssemblyCycleSolver.cs](../Cadoryx.Commands/AssemblyCycleSolver.cs)、[M10](M10_ASSEMBLY.md) | 关系种类、32/128/5 秒边界及局部求解语义 |
| 图纸、材料与 Agent | [TechnicalDrawings.cs](../Cadoryx.Db/TechnicalDrawings.cs)、[Model.cs](../Cadoryx.Db/Model.cs)、[CadAgentToolset.cs](../Cadoryx.ViewModels/Toolboxes/CadAgentToolset.cs)、[M16](M16_DRAWING_AND_EXCHANGE.md) | 当前视图/尺寸与公差、基础材料、有限编辑动作 |
| 版本与阶段 | [Directory.Build.props](../Directory.Build.props)、[SectionMigrations.cs](../Cadoryx.IO/SectionMigrations.cs)、[STATUS](STATUS.md)、[NEXT_STAGES](NEXT_STAGES.md) | 当前包与存储版本；阶段记录和剩余门禁 |

本文新增能力的状态基于上述应用入口核对；对没有当前契约/入口的工具记为“未实现”，不据类名或内核能力推断完整功能已经可用。后续实施时按 G01–G24 回填对应阶段文档、证据路径和剩余边界，再更新本报告。
