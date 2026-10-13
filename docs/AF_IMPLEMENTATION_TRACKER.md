# A–F 全量实施跟踪

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

2026-10-13，status241：[生产根Release6652](../capability-evidence/diagnostics/af1076-root-full/release-verification.json)实际6628通过/原24 STEP失败/零跳过、无新增失败及native62准确；原协调空闲检查退出后只续跑尚未开始的Debug，Release没有重跑。1077最终WPF两配置build0、实际WPF载荷的原编译断言各45通过，provider2共享16逐字节一致；原模型队列实际等待Debug终态，原性能正文已适配并保留预算/完整PNG，仍未运行。生产1076、其他A–F门禁及SDK CJ–CQ整体迁移继续，G/清理暂缓。详见[STATUS](STATUS.md)。

2026-10-13，status239：1076原模型两场景已实际结束，多文档8轮PASS/资产0，工程图第三轮FAIL；实际native62准确。完整CylinderFace原条目已进入三个实际HTTP请求，原查询/位置/5786字符和工具可用性已验，不算尺寸工作流通过；新游标恢复在本次多文档没有被调用。生产根完整6652 Release实际运行，Debug串行待验，未报告最终总数。SDK状态诊断已接收，原两个端盖边InvalidCurveOnSurface(8)、面UnorientableShape(27)及原exact标志保持；缺12上下文的初版日志不是通过证据，没有可采用修复。原A–F全部范围继续，G/清理暂缓；详见[STATUS](STATUS.md)。

2026-10-13，status238：[完整1076组合已合入开发目录](AF_SOURCE1076_DEVELOPMENT_INTEGRATION.md)，不再是尚未合入的source1000组合。55现有/44新增、1189输入一致，生产根两配置构建0及实际6652发现；候选两配置各455个不同专项通过、实际native62核验。原6619名称完整保留，完整6652及根原生验收尚未运行。新历史回执保留cursor:null、过期仅提示只读重查，合入前生产两配置各261通过；不推断原模型可用。1075原两场景均FAIL且completeSourceItems未触发；D6第三版窗口可启动但实际六状态未完成。SDK mapped-scale两方案严格FAIL，继续定位具体状态。A–F所有原范围继续，G/清理暂缓；下方旧生产/候选状态按历史检查点阅读。

2026-10-13，status237：[原Bonsai1074两场景](../capability-evidence/diagnostics/af1074-public85/provider-runtime-verification.json)实际多文档8轮PASS/资产0、工程图第三轮原16轮FAIL，实际native62准确。新[F5完整原生条目补充](F5_COMPLETE_NATIVE_SOURCE_CONTEXT.md)两配置34/34，旧版各33/1；1075完整solution两配置构建0、实际Agent二进制各34/34，原6635来源保持、新13项、预计6648尚未发现/全量执行。窗口宿主输入前启动失败已定位，修订2编译通过但实际窗口仍未通过。SDK原版诊断仍失败，新的精确mapped-scale假设未验证；原STEP24/窗口/材质/性能/生产集成及其他A–F范围保持，G与清理暂缓。

以下status236及更早为历史；当前进度以status237为准。

2026-10-13，status236：[1074/public85合并候选](AF_SOURCE1074_INTEGRATION.md)完整solution两配置构建0警告/错误，实际完整候选Agent二进制两配置各21/21；原6619测试源码保持，新增16案例，预计6635尚未实际发现/执行。原36窗口与原Bonsai两场景已绑定新应用/SDK载荷，实际桌面未开始；服务当前15630、原模型重新以8192加载，工程图工作流正在实际运行，随后串行多文档。父1073[两配置完整6619](../capability-evidence/diagnostics/public-nuget-migration/public85-final1073-full-both-verification.json)各6595/24/0跳过，原用例保持、无新增失败、原AI预算失败已通过、实际native62准确。SDK原版对照两配置原生已结束，报告待独立接收，无已验证修复。1074未合入生产，原严格STEP24、窗口/材质/性能/提供方与其他A–F范围保持；G及Cadoryx清理暂缓。

以下status235及更早记录为历史；当前进度以status236为准。

2026-10-13，status235：[source1073/public85 Release完整6619](../capability-evidence/diagnostics/public-nuget-migration/public85-final1073-release-verification.json)实际6595通过/24失败/零跳过，原6556及新增63名称保持，只有原严格STEP24失败，原十二工具恢复预算失败已通过。冻结源码/Release完整载荷及实际native62核验通过；Debug正在串行运行，最终1073仍未合入生产。[F5默认分页配对](F5_DEFAULT_PAGE_QUERY_MATCHING.md)另进入当前生产源码，两配置独立21/21，旧版各16/5；不替代冻结候选/原Bonsai/窗口验收。SDK会话仅准备STEP误差分段探针，无已验证修复，A–F尚未全部完成；G及Cadoryx清理暂缓。

以下status234及更早记录保留当时状态；当前进度以status235为准。

2026-10-13 用户更新：继续 A–F；开发期需要新封装/重编译可使用本地精确版本 OcctSharp NuGet，接近收尾再统一切回公开包。当前冻结的 Preview.85 完整回归保持来源与预算；G 和 Cadoryx 清理暂缓。详见[开发策略](OCCT_PUBLIC_NUGET_MIGRATION.md#当前开发策略2026-10-13-用户更新)。

2026-10-13，status234：[检查点6](../capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT6.json)核对Cadoryx公开迁移提交 `820e53f` 已推送到origin/master；OcctSharp实现/资格 `587cadd` 及清理收尾 `3e1f49a` 已推送，远端HEAD一致、SDK工作树干净。SDK清理117个可重建缓存目标、37,663,196,524逻辑字节（35.08GiB），最终包/运行时/许可/来源/证据保留；Cadoryx未清理。SDK优先时段已结束，最终source1073/public85的Release完整6619正在运行、实际native62来源/哈希准确，随后Debug6619；独立审计已排队，要求保留原6556名称及新增63、原断言/公差/预算、两配置完整TRX和实际加载载荷。已复现部分原严格STEP导出拒绝；本轮全量尚无终态，不算通过。A–F继续，G暂缓。

以下status233及更早记录保留当时状态；当前进度以status234为准。

2026-10-13，status233：[检查点5](../capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT5.json)确认公开 Preview.85 已实际应用生产，仅nuget.org、稳定SDK10.0.401，生产根50项目/120阶段/Release及Debug共60次编译均零警告/错误；30份SDK锁/412引用和六个输出的13托管/62原生DLL匹配公开包。实际公开能力探针55/55通过。vcpkg独立profile两配置各完整2255/2255、零失败/跳过，磁盘84DLL/22许可证；Release实际观察84模块、Debug实际观察40模块及桥的路径/哈希准确，Debug不证明全部84加载。最终source1073公开85完整6619两配置回归已开始，原严格STEP24、工具预算及实际窗口/性能/提供方门禁仍保持，最终1073尚未合入生产。Cadoryx不需要vcpkg；Native.win-x64继续受支持、不弃用。A–F继续，G及Cadoryx清理暂缓。

以下status232及更早记录保留当时状态；当前结论以status233为准。

2026-10-13，status232：公开 `8.0.1-preview.85` 完整14包已发布并完成签名/远端身份核验；Cadoryx 两组隔离消费者各50项目、120恢复/编译阶段、60次两配置编译通过，最终1073的221类型/696成员集合保持，当前生产为207/661。已将生产41份配置/锁及baseline切换到公开85，正式30份SDK锁/412引用通过核验；正在生产根用全新缓存完成恢复和两配置编译，原生运行尚未开始。[生产应用收据](../capability-evidence/diagnostics/public-nuget-migration/public85-production-applied.json)与[生产消费锁](../capability-evidence/diagnostics/public-nuget-migration/public85-production-locks-verification.json)已保存。Cadoryx 不用vcpkg；`OcctSharp.Native.win-x64` 继续提供原生运行时，无替代包或弃用计划，不标记过时。vcpkg第二profile的Release完整2255通过已由上游报告，Debug实际原生验证正在进行，独立证据待接收。A–F继续，最终全量/窗口/性能/提供方和原严格STEP24/AI预算失败仍待收敛；G及Cadoryx清理暂缓。

以下status231及更早记录保留当时状态；当前结论以status232为准。

2026-10-12，status226：[必要API迁移→公开NuGet→公开消费](OCCT_PUBLIC_NUGET_MIGRATION.md)成为当前前置工作，按用户授权委托现有OcctSharp会话；暂定85/ABI1.118/bridge0.133.0，发布和生产切换尚未完成。最终1073与SDK30组合1066的221/696静态引用一致，行为增量另验；两配置SDK30完整6530均通过，原solver21 Release继续运行，其后消费者等待上游优先时段。A–F全部范围及原1e-7/工具预算保持，G和Cadoryx清理暂缓；上游不需要的编译产物及`.gitignore`交由该会话按新授权处理。以下status225及更早为历史检查点。

2026-10-12，status225增量：[D6/SDK30 Release全量6530/6530](../capability-evidence/AF_PENDING_D6_SDK30_INTEGRATION_FULL_RELEASE.json)通过，零失败/跳过；完整来源/名称和实际native62再次复核。Debug全量正在运行，solver21原6556及最终1073的6619仍按队列续接；真实窗口、提供方、性能与生产接入没有因此通过。生产仍1000/SDK26，A–F继续，G和清理暂缓。

2026-10-12，status225：**A–F继续；G及清理暂缓。** [D6/SDK30组合](D6_SDK30_PRODUCTION_INTEGRATION.md)source1066两配置build0警告/错误，发现原6467+63=6530，实际各有限曲面99/99、原AI/工程图165/165及native62已独立核验；完整6530正在新独立队列运行，之后续solver21原6556两配置新attempt2和最终1073的6619两配置。[检查点](../capability-evidence/AF_PENDING_STATUS225_CONTINUATION_CHECKPOINT.json)保留原无终态/空目录和新冻结运行器；原三个协调进程已消失，未推断中断原因，也未覆盖旧证据。

[窗口观察器修订5](../capability-evidence/AF_PENDING_D6_WINDOW_OBSERVER_V3_BUILD_VERIFICATION.json)已修正模态调用顺序并通过两配置编译/磁盘载荷审计；36个创建/编辑三语言场景、六状态及30/300秒保持，实际WPF尚未运行。[旧source1070完整6556](../capability-evidence/AF_PENDING_F5_SOURCE1070_FULL_PROVIDER_VERIFICATION.json)两配置各6432/124；其原跨文档Bonsai8轮通过，工程图仍失败，不能替代最终1073提供方验收。[精确插结点14组](B18_EXACT_KNOT_SUBDIVISION_DIAGNOSTIC.md)全部拒绝，包含6个旧读取器空几何假阳性。生产仍1000/SDK26，正式已验子批仍A4/B17/C5/D5/E4/F5g，第4、5节及G01–G24全部范围保持；以下status224及更早为历史。

2026-10-12，status224：**A–F继续，G及清理暂缓。** [D6新实体与自由起始平面](D6_FINITE_SURFACE_NEW_BODY.md)在隔离source1073 / SDK31 / solver21实现，两配置构建0、发现原6556+新63共6619；最终两配置各有限曲面99/99及AI/工程图189/189，实际native62及完整1073恢复重放已核验。新实体只保存原Until及完整有限域证明后的delta，私有seed不进入作者/输入；精确重放、失效冻结/明确重选及晚到取消资产释放保持。两处重复schema说明精简修复原188/1工具遗漏，真实成本5606→5596，原激进预算5597及算法保持。[汇总证据](../capability-evidence/AF_PENDING_D6_FINITE_NEWBODY_FUNCTIONAL.json)保留原失败；短场景只余1 token，实际Bonsai另验。完整6619两配置已排队，实际窗口/性能/生产接入未验，生产仍source1000/SDK26。以下status223及更早是历史检查点。

2026-10-12，status223：**A–F 继续推进，尚未全部完成；G 实机、安装及交付暂缓，清理保持停止。** 隔离source1070 / SDK31 / solver21收紧完整作者处方守卫；两配置完整solution构建零警告/错误，原6556来源/名称保持。原986功能专项两配置各962通过/24失败，原99精度失败全部消除、原306耦合正例全部通过；F5路径及上下文两配置各29/29。剩余24仍为原精确STEP导出拒绝，守卫、公差、迭代预算保持。两文件字节补丁已实际重放全部1070；完整6556两配置已排队，当前未运行，生产仍source1000 / SDK26。原全量和Bonsai队列保持，不以旧载荷或本轮功能诊断代替最终窗口/材质/性能及提供方验收。详情见[完整作者守卫记录](B18_COMPLETE_AUTHOR_SOLVER_GUARD.md)与[独立功能审计](../capability-evidence/AF_PENDING_SOLVER21_COMPLETE_AUTHOR_FUNCTIONAL.json)。

[F5工程图来源路径引导](F5_DRAWING_SOURCE_PATH_GUIDANCE.md)针对原Bonsai将单个实例槽ID误当完整path的实际失败，保留原拒绝并提供未执行的occurrences/bodies读取请求。schema明确完整occurrences.items[].path；旧文档/状态、关闭/取消、缺失/超长输入、完整路径的实体/修订及原检查器自身错误保持，不推断选择或自动重绑。[独立核验](../capability-evidence/AF_PENDING_DRAWING_SOURCE_PATH_FEEDBACK_VERIFICATION.json)绑定source1070两配置build0、原6532/986名称保持、新增24及各29托管检查；同载荷只换旧ViewModels均19/10，确认八条路径和两条schema引导原本缺失。第一版各26检查与旧16/10对照保留，第二版只增三个合法路径分派/实体身份守卫案例，原21断言逐字节保持。

[四文件恢复补丁](../capability-evidence/AF_PENDING_DRAWING_SOURCE_PATH_FEEDBACK.patch)在已验SDK31应用清单恢复副本上实际应用，全部1070来源哈希一致，原作者、求解器、STEP守卫和公共上游保持。[原Bonsai程序构建核验](../capability-evidence/AF_PENDING_DRAWING_SOURCE_PATH_PROVIDER_BUILD_VERIFICATION.json)保留原三份C#流程/提示/断言和相同模型、8192/16预算；当前16应用/13SDK/62原生磁盘载荷及锁定14包通过。原165加新24的189相关、完整6556两配置及实际Bonsai两场景已顺序排队，当前NOT RUN，不把托管29或磁盘载荷核验当作这些门禁通过。此前source1068 SDK31活作业也没有终态；A–F剩余全部范围继续，清理未执行。

以下status221及更早内容保留当时状态；本轮1070与原提供方构建以以上证据为准。

[SDK31应用依赖接入核验](../capability-evidence/AF_PENDING_SOURCE1068_SDK31_APPLICATION_BUILD_VERIFICATION.json)绑定原1068来源、两配置完整构建和测试发现；只改变Directory.Build.props、NuGet.Config及五份packages.lock.json，非Occt依赖版本/contentHash保持。应用与测试目录中14包/13托管/62原生均匹配独立私有包；应用Debug仍消费包内Release原生，真正Debug-native1622另有SDK证据。原65、相关986和完整6532的两配置门禁按顺序排队，尚无本轮运行终态；不得引用旧SDK30运行结果代替。草案1的runtime-baseline.json保留SDK30身份，未作为最终候选；[草案2身份清单修复与核验](../capability-evidence/AF_PENDING_SOURCE1068_SDK31_APPLICATION_MANIFEST_VERIFICATION.json)只更新包名、ABI1.89、bridge0.97.0及已验包内Release原生桥哈希。完整两配置再次构建零警告/错误，原6532/986用例名称及其他1067来源字节保持，实际输出清单与13托管/62原生包载荷一致。独立来源副本与[清单补丁](../capability-evidence/AF_PENDING_SOURCE1068_SDK31_MANIFEST.patch)再次重放全部1068哈希一致；先依次应用原source1068、新SDK31字节依赖补丁，再应用清单补丁。草案2实际运行仍NOT RUN，已排队的草案1没有停止、修改或重启，其未来结果也不能代替草案2最终窗口/材质/性能验收。

[应用SDK31字节恢复补丁](../capability-evidence/AF_PENDING_SOURCE1068_SDK31_DEPENDENCY_V2.patch)及[重放核验](../capability-evidence/AF_PENDING_SOURCE1068_SDK31_DEPENDENCY_V2.json)已从Git dcbc948基础提取1029份输入，实际应用原source1068补丁及新268文件字节恢复补丁，全部1068来源哈希核对一致。268包含七个真正依赖更改与267份原Windows换行恢复，部分路径重合；没有改测试语义或放宽断言。初轮原源码补丁与Git基础产生267份LF/CRLF字节差异及NuGet本机feed规范差异，记录保留；新补丁以Git binary literal恢复既有编译输入，只规范NuGet feed为同一私有14包的根相对路径。补丁SHA256为`B39CB9916CC8D0CB46981C23F861C455B945154DF2EA568227DC37B10DAA0B4A`，未应用生产。

[精确平面替代表示诊断](../capability-evidence/AF_PENDING_SDK31_PLANAR_REPRESENTATION_REJECTIONS.json)核验三种方案及对照共14组writer/stock-reader，均包含Release/Debug真实模块路径与哈希，固定原版OCCT读取器没有SDK支持或桥。原UV结点的一次张量积样条平面、其直接3D边变体、无限直线拉伸平面都保持私有严格BRep、作者BRep、原pcurve/范围/公差，曲面点/导数采样差为零；但外部端盖曲线/pcurve误差仍约0.000504511924 mm，严格失败。原SDK31对照仍6.9932867123069075e-7 mm严格失败。诊断来源已保存为可审阅文件，三个方案全部拒绝纳入生成器或产品；未拟合、提高公差、改变STEP守卫或公共上游。非解析pcurve、外部精确等价及A–F其他范围继续。

以下status220及更早内容保留当时状态；本轮应用构建/恢复和三种拒绝诊断以以上证据为准。

[SDK31版本/私有包/干净消费者独立核验](../capability-evidence/AF_PRIVATE_SDK31_PACKAGE_CONSUMER_VERIFICATION.json)只对已验草案7的1292来源修改三个身份文件；原行为断言和1622用例名称保持，两配置真实原生/托管构建及各248生成器、252新增公开与1622全量均通过，零失败/跳过。实际62原生路径与哈希及61依赖保持；13模块公开API无增删，4177 facade转发由实际模块重新生成核对逐字节一致，186标准生成输出/26040声明ID及41176导出名称/顺序保持。私有14包内DLL与已验13托管/62原生逐一一致，许可证及[当前契约](../capability-evidence/AF_PRIVATE_SDK31_EXACT_STEP_CONTRACT.md)齐全。旧SDK30包的模块依赖实际为最低版本范围；本轮增加仅用于私有打包的MSBuild导入，所有SDK依赖精确锁定`[8.0.1-preview.31.cadoryx.exact-step.31]`，真实打包正例和异版本拒绝负例已验，不改运行时或生成绑定。

全新consumer2/cache2只从私有feed自动交付原生DLL，普通及锁定restore、Release/Debug消费均通过；两配置使用包内Release原生，真正Debug原生的完整1622另有证据。原42历史/28序列化/32实例/10初始呈现/5恢复健康族/12删除面案例保持，再运行全部252原始新测试体、输入与断言。实际62原生DLL、锁文件14版本/包哈希及60张完整320×320图像已核验，图像与先前已验SDK30完整图逐字节一致。首轮测试适配器缺少TheoryData而编译失败；consumer2仅补同类型/值/顺序的数据容器，11个严格断言负例和边界正例通过。一次审计误读consumer1失败发布日志后，只校正日志路径核验已有终态，没有重跑消费者、改测试或清理。

[第十一版恢复补丁](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V11.patch)和[元数据](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V11.json)保存259文件（含真实生成输入/输出、版本身份、永久回归、原生探针及精确依赖打包导入），独立重放逐字节一致，SHA256为`FE7A7F84450B348D106177DFD765E8187C393CFCF31EBE4EC90B7CBC94A91B4C`。原草案7及更早失败保留。未放开应用导出守卫、未改公共上游或控制桌面键鼠；应用原65/986/6532、实际材质UI/恢复/性能、原Bonsai和生产接入继续。外部原版OCCT端盖6.993286712306907e-7 mm严格失败、写出共享单位冲突拒绝及一般非解析参数曲线未完成仍明确保留。

以下草案7及更早内容保留当时状态；当前SDK31版本/包/消费者以草案8证据为准。

[SDK31草案7独立核验](../capability-evidence/AF_PENDING_SDK31_MOMENT_SHARED_PUBLIC_VERIFICATION.json)将已有非生成积分算法的10个矩改为补偿累加，并跨积分层级携带累计余数；几何评价、Gauss节点、1e-11产品条件、深度20/2000000预算及原1e-7作者门禁不变。原12根矩阵两配置均12/12，毫米系统大圆柱的原3.814697265625e-6 mm³失败消除。新增16个永久公开案例覆盖三种系统单位、正反根顺序、重复传输、释放会话后的独立所有权，以及普通/报告整文件读取的三个严格实体；同测试载荷仅换回冻结草案6桥，实际native62下原4个严格体积失败复现，新桥两配置各16/16。完整两配置各1622/1622、零失败/跳过，原1606名称/来源保持；托管均0警告/错误，原生两配置及62份实际模块路径/哈希、61依赖、每配置425文件短载荷已验。186生成输出及26040声明ID逐字节/集合保持，41176导出名称和顺序与草案6完全相同。

[第十版恢复补丁](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V10.patch)和[元数据](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V10.json)保存255文件，独立重放逐字节一致，SHA256为`B1BEA2C0C0092355A0D816BF7C4AFBA229DC4F6A462D3BBF9911922977C4A023`。原[第九版](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V9.patch)/[记录](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V9.json)保留未修正CA1861的初版测试；仅把测试的重复常量数组改为静态只读，未更改断言。首轮托管CA1861构建失败、共享探针在最终DLL未就绪时的3221225781加载失败均保留；未启动桌面键鼠、未清理或改公共上游。SDK31身份/包/干净消费者和应用接入尚未运行，外部原版OCCT严格失败继续，其他A–F范围按既有规划推进。

以下草案6为保留的历史检查点；当前共享根及公开回归以草案7记录为准。

[SDK31草案6完整回归核验](../capability-evidence/AF_PENDING_SDK31_SHARED_UNIT_FULL_VERIFICATION.json)已收齐两配置实际原生/托管构建及完整各1606/1606，零失败/跳过，原1606名称和来源保持，实际native62路径/哈希绑定本轮原生桥。托管两配置均0警告/0错误，原61依赖及每配置420文件短载荷同哈希。实际导出为41176：原41174保留、零移除、C ABI名称/顺序保持，另有Interface_FloatWriter与Interface_LineBuffer两个OCCT编译器构造辅助符号，不能称导出数量完全不变。两配置各108共享曲线通过、实际三根各10/12和两个原1e-7体积失败保持；完整SDK旧回归通过不替代这些新增失败。永久公开共享案例、复杂装配/PMI/角单位共享及非解析参数曲线、SDK31身份/14包/干净消费者、source1068与其他A–F门禁继续，外部原版OCCT端盖严格失败没有消除；清理及G暂缓。

[SDK31草案6共享单位核验](../capability-evidence/AF_PENDING_SDK31_SHARED_UNIT_VERIFICATION.json)从生成器实现读入时按实际表示单位建立私有STEP实体副本，保留同单位共享、隔离不同单位，并核验显式Part21标识、实体内容、根身份/顺序与原读取器恢复；写入侧单位冲突仍拒绝。两配置原108项均18通过/90失败，当前均108/108通过。实际三根文件的12项两配置均10通过/2失败：原毫米/英寸/米系统单位及正反顺序、原1e-7体积门禁保持；大圆柱在毫米系统的产品积分误差3.814697265625e-6 mm³仍失败。临时会话、最终传输图及未引用实体处理已修复；私有写出副本的二维圆/直线offset参数曲线采用精确解析恒等式，没有拟合、改范围/公差或修改作者BRep。SDK全量构建和两配置完整1606现已独立核验通过，详见本页最新记录；永久公开共享案例、包/干净消费者及应用集成仍未验，新增两个严格失败继续，不能宣布新候选或A–F完成。

[第八版恢复补丁](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V8.patch)和[元数据](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V8.json)保存相对SDK30的248文件，包括可单独编译的108曲线及12实际根原生探针，独立重放全部逐字节一致；[第七版代码补丁](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V7.patch)及[记录](../capability-evidence/AF_PENDING_SDK31_EXACT_STEP_V7.json)只含此前245文件，未含三份新探针来源，保留其原字节。两配置生成器各248通过，标准生成两轮186输出一致、原184份保持、26040声明ID不变。原AP214空CopyCase崩溃、首轮缺少退出元数据、源夹具漏包含及一份已核对PID/父进程/路径的故障Debug诊断被停止等记录均保留；没有停止用户窗口或清理文件。

[SDK30基线材质专项](../capability-evidence/AF_SDK30_BASELINE_MATERIAL82_VERIFICATION.json)已实际通过原82/82，原案例/断言/预算只调整严格SDK身份；每例零资产，实际30托管/62原生路径与哈希一致。346张完整480×320 PNG与先前已验整图逐字节一致，未以缩略图代替整图。绑定source1063 / SDK30及原两配置6467；不是source1068 / SDK31最终验收。隐藏消费者没有控制键鼠，实际材质UI、性能、上下文/GPU故障及生产合入仍待验；历史脚本中的SDK27标签保留，实际身份由版本断言和载荷核验确定。

[固定原版OCCT源码复核](../capability-evidence/AF_PENDING_SDK31_STOCK_PLANAR_PCURVE_LIMITATION.json)确认CheckPCurves对平面主动删除已有pcurve，MakeCurve2d也没有offset分支；单补写平面参数曲线不能消除原独立严格失败。保留两配置原6.993286712306907e-7 mm端盖误差，没有拟合、提高公差或放开导出守卫。精确出站/外部等价仍待完成，同时继续合法共享单位上下文克隆、SDK31包/消费者、source1068接入及A–F其他门禁。

当前[Release完整6467/6467](../artifacts/af-sdk30-history-identity-draft-5/full6467-release-1-verification.json)与[Debug完整6467/6467](../artifacts/af-sdk30-history-identity-draft-5/full6467-debug-1-verification.json)均独立通过，零失败/跳过、原用例名称及实际SDK30/native62保持。[原Bonsai两个实际结果](../artifacts/af-sdk30-history-identity-draft-5/provider-both-1-exit.json)均失败：工程图在创建阶段缺少成功收据，跨文档第八轮超原16工具轮预算；不修改提示、身份守卫或预算。当前1066的相关980失败后，[最终应用准备](../artifacts/b18-single-loop-mixed-joints-draft-2/final-app-after-baseline-1-exit.json)按原门禁停止，未启动1066发布或桌面。已发布1063只作基线，不能代替后续修正候选的最终验收；原24和新36须绑定最终载荷后重编译/实际运行。

当前[原WPF24及主壳状态栏36验收程序](A5_MAIN_STATUS_RECOVERY_ACCEPTANCE.md)均已编译、引用载荷已验，运行尚未开始。原24的C#逐字节保持、48保存/52整图及30秒预算不变；新36通过真实生产MainWindow、实际原生失效/绘制消息核对三种语言和两个活动pane的状态绑定、文档历史与144整图。首轮空引用编译失败保留，未调用私有恢复或写状态文本；原上下文/GPU故障仍未验。

[B18重基1066](../artifacts/b18-single-loop-mixed-joints-draft-2/pipeline-after-provider-1-exit.json)两配置构建零警告/错误、发现原6467加59的6526，各17托管已独立通过。原Release相关980实际为953通过/27失败：42混合原生案例中有求解不收敛、非圆/直线偏移导出拒绝和独立源面积精度超限；原日志/TRX保留。Debug相关980及两配置完整6526未运行，未证明全部42或B18完成。两个窗口辅助程序及原窗口/性能条件保持；未控制键鼠。下一步先诊断修复，保留原矩阵、公差和256变量/512方程/80迭代，再验完整门禁。

[source1068求解候选](../artifacts/b18-prescribed-author-draft-4/focused-1-verification.json)只对端点关系中全部作者坐标都有明确唯一FixPoint的系统保留精确值；实际表达式端点提供者不进入该扩展，全部变量/方程/完整Jacobian与最终原生Refresh/ValidateSolved保持。原36正例均2次迭代完成，原17和新增6冲突/关系拒绝通过；原65为41通过/24导出失败。首版把CurveGenerated来源标志误当实际提供者，未启用优化的原失败保留；修订4按真实提供者ID区分。原6526测试源码逐字节保持，[Release发现6532](../artifacts/b18-prescribed-author-draft-4/inventory-release-6532-1-verification.json)仅增6；Debug、相关986和完整6532未运行，部分来源驱动及一般混合求解仍未证明。

[实际SDK30精确STEP探针](../artifacts/b18-mixed-diagnostic-draft-3/raw-step-1-verification.json)两个样条场景均在原生WriteStep失败，返回IFSelect_RetError/status2；部分文件各含4个空的SURFACE_OF_LINEAR_EXTRUSION基曲线引用，无OFFSET_CURVE_3D。这证明放开应用守卫不能解决导出，部分文件不作产品输出。下一步从生成器输入/模板补精确转换及完整图引用、实际重读验证；已有不支持freeform的拒绝断言不改，没有修改上游或生成产物。

[待合入源码补丁](../capability-evidence/AF_PENDING_SOURCE1068.patch)和[恢复说明](../capability-evidence/AF_PENDING_SOURCE1068.json)保存完整1068候选相对当前生产的56个现有修改、39个真实新文件，973个已有文件保持；1000个生产来源SHA已复核，补丁通过Git索引应用检查但尚未应用。只将NuGet.Config本机绝对feed规范为指向同一14包的根相对路径，不含缓存或二进制。当前生产仍1000/SDK26，提交检查点不代表A–F完成。

| 大批 | 正式已验子批次 | 当前证据及后续范围 |
| --- | --- | --- |
| A 交互与性能 | A4；A5继续 | 当前1080已保留1076合入的材质持久化/恢复与视口故障处理；1076两配置6652终态并有原STEP24失败。原圆盘/综合窗口与历史1056两次4.752/5.061秒有各自载荷证据；1079当前材质UI、完整性能、上下文失效和主壳恢复待验。原6482ms、两fresh各20周期及完整788×411 PNG保持，待SDK交还时段并绑定最新载荷 |
| B 草图与旋转 | B17；B18继续 | 混合作者求解与原99精度失败修正已随1076合入，原STEP24精确交换拒绝仍在两配置完整6652复现。SDK最小mapped-scale和插结点方案均未提供可采用修复；原1e-7不变，更广周期/混合关系/间接表达式和外部完整等价继续 |
| C 参数、基准、阵列与孔 | C5实体螺纹 | C1–C5原本地参数/基准/镜像阵列/标准孔及真实牙型契约保持，后续组合已进入1076/1080开发。最新单项托管208不是C批重新验收；更广驱动字段、关联失效/晚到与组合样本继续，不推断全部C完成 |
| D 实体建模 | D5；D6继续验收 | D6新实体/自由起始平面已随1076合入；原99专项及1076完整6652已取证，原36真实窗口未通过，观察器仅补只读诊断。扫掠/放样/抽壳拔模/实体偏移与SDK26路径持久化保持；任意终止曲面、自由曲面起始、所有Far、完整历史材料第三复制跨作者及更广输入继续 |
| E 拓扑、历史与装配 | E4；E5继续验收 | 受控逐子形状删除面历史及持久化已随1076合入；SDK旧原生/托管1370及包、应用专项和1076完整6652属于各自证据。当前组合窗口、更广来源/历史/装配奇异和失效边界继续，多路径/分裂/合并不猜测重绑 |
| F 工程图、交换、命令与AI | F5g本地作者契约；供应商未收敛 | 1076原模型工作区8轮PASS/工程图第三轮FAIL；1077最终一致载荷工作区4/8轮与工程图1/8轮均FAIL，未采用说明候选。1078/1079/1080修正命令目录、类型反馈及终端成功状态、当前各208托管通过，原8192/16轮/6000与2400回执预算及身份守卫保持；当前模型未复验。制造规则、原STEP24及外部层级/共享/逐面色/PMI/完整几何等价继续 |
| G 实机、安装与交付 | 用户暂缓 | 本次本地提交不代表安装、签名、发布或新机器验收 |

[最新清理](../artifacts/storage-prune-20261011-5-verification.json)另删4682份已被取代的SDK29与失败修订3/4编译文件、1.489 GB，12614份保留文件哈希复核一致、Git状态未变；[随后完整测量](../artifacts/storage-audit-20261011-5-after.json)为14.244 GB/13.266 GiB。清理期间当前构建新增产物，不能将总占用变化作为删除量；旧候选重放须重新编译。源文件、模型、原失败/TRX、截图、包和故障转储均保留，没有停止进程或修改其他仓库/全局缓存。

原始失败及较早隔离/窗口/交换证据见[状态页](STATUS.md)和[暂停检查点](AF_CHECKPOINT_2026-10-11.md)。[本轮另删4371份旧bin/obj及消费者输出、2.409 GB](../artifacts/storage-prune-20261011-4-verification.json)，8626份保留来源/载荷/结果哈希复核一致；[完整目录核验](../artifacts/storage-prune-20261011-4-storage.json)为13.310 GB / 12.396 GiB，无漏扫和错误（核验时快照，后续构建另计）。旧1063修订2和B18草案1重放前需要重编译；源文件、原失败/TRX、模型/截图/包及当前进程保持。

OcctSharp 新底层封装从生成器配置/模板/类型映射修改后再生成，forwarder也走真实生成工具；高层API与生成输出的边界保持。SDK29各1358、包及干净消费者已验；SDK30各1370及包已验。原失败保留，应用集成与尚未运行的设备门禁分别记录。

旧状态和逐项G01–G24证据保存在[截至 status195 的跟踪快照](https://github.com/yoi102/Cadoryx/blob/69268920d83071734f3ddf226d2d89df21109a6e/docs/history/AF_IMPLEMENTATION_TRACKER_THROUGH_STATUS195_20261011.md)；它记录历史当时状态，恢复时先以本页和暂停检查点核对。
