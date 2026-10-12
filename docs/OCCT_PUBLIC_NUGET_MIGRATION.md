# OcctSharp 必要 API 迁移与公开 NuGet 接入

2026-10-13，status233。必要生成器 API 已迁移，公开 Preview.85 完整14包已发布；两个上游真实公开消费者和 Cadoryx 两组50项目的公开恢复/两配置编译通过。Cadoryx 当前生产已切换到公开 `8.0.1-preview.85`，根目录50项目/120阶段/两配置60次编译已通过，公开包能力55/55及native62通过；最终source1073完整6619两配置原生回归已开始，最终 A–F 产品验收仍待完成。所有既有公差、断言、工具预算保持，G及Cadoryx清理暂缓。

- [公开恢复](../capability-evidence/diagnostics/public-nuget-migration/public85-remote-restore-verification.json)：fresh cache、仅nuget.org、普通/锁定恢复、14份签名包和完整native62磁盘载荷匹配受测清单，bridge SHA256 `D44F3F272386EA2CC87E7107AB2E3375D8100283872372F1FBC234A3B4E38CB3`、ABI `1.118`、bridge `0.133.0`。
- [最终source1073及工具](../capability-evidence/diagnostics/public-nuget-migration/public85-final1073-verification.json)：稳定SDK10.0.401，50项目、120阶段、60次零警告/错误编译，应用/测试代码和断言保持；221类型/696成员集合与原SDK31逐项一致，未合入生产或执行原生代码。
- [当前生产及工具](../capability-evidence/diagnostics/public-nuget-migration/public85-production-tools-final-verification.json)：同50项目/120阶段通过，实际207类型/661成员；非SDK既有版本/哈希保持，两个旧工具补入生产IO已有的8条PDFsharp依赖闭包。
- [生产应用](../capability-evidence/diagnostics/public-nuget-migration/public85-production-applied.json)与[全部SDK锁](../capability-evidence/diagnostics/public-nuget-migration/public85-production-locks-verification.json)：41份配置/锁应用、30份SDK锁/412引用及50个直接引用匹配公开身份；最终1073功能源码尚未合入。

`OcctSharp.Native.win-x64` 继续受支持：它是公开包族的Windows x64原生运行时分发包，MSBuild自动把OCCT和桥接DLL复制到输出/发布目录。vcpkg用于OcctSharp开发者还原OCCT/C++依赖和构建SDK；Cadoryx只消费NuGet，不需要vcpkg或本地OcctSharp源码。当前没有替代运行时包或弃用计划，因此不标deprecated。若今后替换分发包，应先提供替代包及迁移说明，再对旧包标记弃用。

签名NuGet存在两种不同SHA512：锁文件 `contentHash` 使用NuGet验证的内容哈希；仓库签名后的整包字节SHA512另行验证，不能填进锁文件。能力探针已从真实assets.packageFolders解析包，不再读取兄弟仓库的打包目录；真实签名正例、错锁哈希和篡改ZIP拒绝场景已通过。首轮隐式library-packs源守卫失败保持，使用正式SDK属性 `DisableImplicitLibraryPacksFolder=true` 修正，无放宽来源门禁。

vcpkg独立profile首次Runtime2253/2255的2个媒体时间失败保留；固定FFmpeg依赖后已独立核验Release及实际Debug-native各2255/2255、零失败/跳过，每配置磁盘84DLL/22许可证；Release实际观察84模块、Debug仅观察40模块且全部路径/哈希准确，Debug不证明所有84模块被观察加载。公开85仍使用独立受测的预编译native62，不能混用两profile的DLL数、哈希或运行结论。

[检查点5](../capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT5.json)保存生产实际恢复/编译、55场景包能力及vcpkg两配置完整TRX/载荷/实际加载核验；这些证据不代替最终A–F完整回归、窗口和外部提供方。

## 历史检查点

以下记录描述当时状态，不代表当前生产仍SDK26或公开包尚未发布。

2026-10-12，status231。用户要求先在 OcctSharp 完成所需 OCCT API 的生成器封装与迁移，再发布 NuGet，随后 Cadoryx 从公开包消费并提交推送。另授权 OcctSharp 清理不需要的编译产物并完善 `.gitignore`；Cadoryx 的清理仍暂缓，A–F 与第4、5节范围保持，G暂缓。

当前进度：[检查点4](../capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT4.json)独立核验了生成器 TransferRecords 模板补齐8个 TopoDS 头后的正规再生成、Release/Debug完整构建与各Generator410/Runtime2255、实际Debug2255和进程加载native62、修正后的冷源完整生成/编译/回归。最终公开桥哈希为`D44F3F272386EA2CC87E7107AB2E3375D8100283872372F1FBC234A3B4E38CB3`；旧`D7AF…`及先前本地包只作为历史兼容记录，不能用于最终公开恢复验收。完整原生导出保留、无移除。

固定vcpkg空目录源码恢复、7478份完整原始头文件、两配置桥和399生成文件对照已通过。Release/Debug各79份DLL及21份许可证的来源、文件名与哈希独立核对通过；实际vcpkg Runtime仍待串行执行。公开85仍使用单独受测的预编译provider/native62，新vcpkg不复用其运行证据或DLL数量。上游首次真实窗口失败保留，主显示器位置修正不证明跨显示器或原重绘根因修复。最终14包重包、远端发布和公开消费者尚未确认，生产仍SDK26。

随后[首次vcpkg Release完整运行](../capability-evidence/diagnostics/public-nuget-migration/vcpkg-initial-runtime-failure.json)2253/2255通过、2失败、零跳过；完整2255原断言保持。失败是正负64位媒体时间转换，固定OCCT源码确认`Media_FormatContext::FormatUnitsToSeconds`在禁用FFmpeg时返回零。79份隔离磁盘DLL与该轮manifest匹配，失败入口未生成成功的实际加载模块报告；不能把磁盘匹配写成进程加载核验通过。依赖配置修正及两配置真实回归继续，vcpkg尚未交付。

Cadoryx 的当前生产及全部工具已在隔离目录完成50项目、120恢复/编译阶段、两配置60次零警告/错误构建，30份 SDK 锁统一本地85候选。六个旧工具的固定私有版本表达式已在该目录改为共享版本，ThreadDrawingProbe/AnalyticalOffsetProbe显式绑定冻结应用，后者补齐实际所需 Kernel.Abstractions 引用。两个旧工具锁补入生产IO已有的PDFsharp依赖闭包8条，已有非SDK版本/哈希未升级；这些配置尚未应用生产。

下一公开消费者已冻结最终source1073及所有工具，共1186输入/50项目；准备时仅9个配置文件变化，应用、测试、探针代码与断言原字节保持。实际入口必须绑定全新公开恢复通过的清单/包哈希，已经验证旧本地候选证据在恢复前被拒绝且未创建缓存或改变baseline。这个准备结果不代表公开恢复、编译或运行通过。

上游工作交给现有“重新梳理项目进度与规划”会话，ID `01a0f7b3-ff5f-7cb0-bff9-a5caad1a49b0`。该会话核对了人类原始授权，负责必要生成器输入/模板/类型映射、再生成、原生/托管与包消费者验证、公开发布及源码提交推送。暂定公开版本 `8.0.1-preview.85`、ABI `1.118`、bridge `0.133.0`；这些是准备身份，**当前未确认公开发布，也未切换 Cadoryx 生产依赖**。

## 已核对的需求

[编译引用及阶段清单](../capability-evidence/AF_PENDING_PUBLIC_NUGET_REQUIREMENTS.json)绑定最终source1073/SDK31和source1066/SDK30的已编译载荷。工具只读取 PE 元数据，不加载 SDK 或调用原生代码。各38个DLL文件包含21个不同二进制，合并得到221个不同类型、696个不同成员引用，其中634个由应用或内置诊断引用，62个仅由测试引用。两候选类型/成员集合完全一致，Cadoryx 没有直接 OCCT 原生 P/Invoke。这里的引用数不证明运行覆盖、未来能力覆盖或整个 OCCT 迁移完成。

上游初步确认必要增量包括 `AuthoringInputCopy/HistoryDomains`、`DefeatureHistoryModeling` 和首次 `DisplayInMode`；行为增量包括精确 offset STEP 图、单位/共享副本、周期精确距离及积分矩补偿。已有公共能力应复用，不能只按同名方法存在判断行为兼容，也不重复制造等价包装。迁移输入/历史等私有手写支持到模板，保留公共既有 STEP/XDE/Viewer 生成委托及所有所有权、线程、身份和错误边界。

SDK31 原24严格STEP拒绝、外部原版OCCT端盖pcurve误差及全部1e-7门槛保持。14组精确插结点方案均拒绝，包括6个空几何假阳性。公开包可用和必要绑定迁移必须分别验证，不表示这些外部算法问题、A–F全部范围或实际窗口验收自动完成。

## 接入顺序与门禁

1. 完成必要API/行为迁移，生成器再生成并核验实际Release/Debug原生/托管、相关回归及完整交付范围。已编译候选和旧失败记录保持。
2. 发布统一版本的完整包族，确认 nuget.org 实际可恢复及包依赖/载荷身份。凭证仅在需要用户完成的登录或API-key动作时处理，密钥不写入仓库或输出。
3. 用全新包缓存、只配置 nuget.org 的消费者普通及锁定恢复，核对14包版本/contentHash、公开来源和发布方受测清单中的全部原生DLL集合/逐文件SHA256。旧SDK分布仍绑定原62闭包；新vcpkg分布须独立绑定实际受测闭包。随后绑定最终候选完成编译、运行、寿命、持久化及相应窗口/性能门禁。
4. 将生产 `NuGet.Config`、`OcctSharpVersion`、全部相关锁文件及实际runtime-baseline更新到已验证公开身份；移除运行/构建对兄弟仓库及私有feed的依赖。历史证据中的旧路径保留为当时的来源，不作为新消费路径。
5. 提交推送两仓库的对应源码与文档。OcctSharp `.gitignore` 忽略可重建编译/缓存产物，待发布验证不再需要它们后按授权清理；保留源码、原始样本、包与直接证据，不停用户窗口或活动消费者。

[`scripts/nuget-package-cache.ps1`](../scripts/nuget-package-cache.ps1)已从实际 `project.assets.json` 的包缓存解析 nupkg，并逐项验证版本、锁定SHA512及包字节，替换能力探针对旁边OcctSharp打包目录的读取。六个纯元数据夹具通过：正确归档、错版本、assets/lock哈希冲突、外来路径、错归档字节、缺少归档；真实SDK30恢复缓存也成功解析。夹具及私有缓存通过不代表公开包消费通过。

[`scripts/verify-public-occt-restore.ps1`](../scripts/verify-public-occt-restore.ps1)已准备公开恢复门禁，仅语法核验，实际运行等待发布及发布方native桥哈希。它固定公开源和全新缓存，验证普通/锁定恢复、14包及native62磁盘载荷；运行与应用验收另做，不能将磁盘核验当成运行通过。

该恢复门禁现要求精确14个包ID，数量相同但模块缺失/替换也拒绝。[全部消费锁门禁](../scripts/verify-public-occt-locks.ps1)核对生产共享版本、仅公开源、baseline桥身份，以及全部应用/工具消费锁的公开包SHA512；历史冻结诊断项目不作为活动消费者。接入前清点实际有30份SDK消费锁、50个直接包引用，其中旧工具仍有私有版本固定值，发布后需统一刷新，不能只更新solution锁。两个公开门禁目前仅语法核验，实际均NOT RUN。

## 当前队列

SDK30两配置完整6530均独立通过，零失败/跳过。原solver21完整6556 Release attempt2正常运行；当前消费者不被中断。为新的优先顺序，调度helper在下一次原生消费前等待上游迁移/发布时段，原6小时调度上限、原测试/命令预算及全部范围保持。新的独立预约到18:00 JST，上游使用前仍需等待当前套件终态和实际消费者退出，结束或续约以协调记录为准。

原Debug/最终D6回归和已排队的SDK30窗口观察器继续保留，未执行部分不会被记为通过。窗口实际输入仍在开始前提示用户；最终公开载荷必须重新绑定相应验收。生产目前仍是 `8.0.1-preview.31.cadoryx.curve-inspection.26` 私有源。

15:25:42 JST，原solver21 Release完整6556 attempt2结束，实际6531通过、25失败、零跳过。失败为24个原混合单环严格STEP导出拒绝和1个原工具预算断言；未修改原名称、断言或预算。[检查点2](../capability-evidence/AF_PENDING_PUBLIC_NUGET_CHECKPOINT2.json)绑定原build、TRX、终态和实际native62文件哈希。随后确认实际dotnet/testhost消费者退出并通知上游开始生成/编译/回归；后续原消费者继续等待新的独立预约。这些结果不作为公开85或最终1073验收。

## 用户新增的 vcpkg 还原要求

用户要求OcctSharp支持vcpkg，让更换开发环境后可还原依赖。[固定基线审计](../capability-evidence/diagnostics/public-nuget-migration/vcpkg-baseline-audit.json)确认官方端口8.0.1的源提交与原SDK一致，为`b8f597c677811d1f9f4d8a97f5ae2825c0353a42`；registry基线固定为`e456309491fd875487bf02b8a0dca76c1b1b00cc`，同时记录第三方版本及端口修订。此审计不是vcpkg恢复/编译通过。

用户进一步确认：**Cadoryx 不使用 vcpkg**。vcpkg仅属于OcctSharp开发构建入口，负责恢复OCCT及其C++依赖、运行生成器和编译原生桥。Cadoryx只通过公开NuGet包消费OcctSharp与随包提供的原生DLL，正常构建不要求本地OcctSharp源码、OCCT开发SDK或vcpkg；迁移完成后的应用运行也不要求这些开发工具。

公共构建、生成器header合同、运行时闭包与打包入口由同一上游会话统一接入。官方端口的安装目录、头文件处理及第三方版本与当前预编译SDK不同，必须独立验明；不把旧SDK的62 DLL或头文件哈希用于新profile。当前公共API原生Release已编译，完整Runtime2251/2255，四个CB/CC选择后重绘断言仍在诊断；vcpkg干净恢复/编译、公开NuGet发布及Cadoryx消费均未通过。独立优先预约已按上游请求原子延长至20:00 JST，原6小时调度上限保持，旧预约字节已备份。

公开恢复脚本现新增必需`ExpectedNativeManifestPath`，与独立受测桥SHA一起验证完整DLL集合及每个DLL的SHA256；消费锁还核对ABI和bridgeVersion。八个纯元数据夹具通过：实际Preview84 manifest，以及错版本、缺少ABI、重复DLL、路径穿越、非法SHA256、零文件大小、不同桥哈希的拒绝。它们不执行SDK/原生代码，不表示公开恢复或vcpkg实际运行通过。旧脚本保留字节快照，完整交付门禁保持。

## 本地候选的实际 Cadoryx 编译

上游14个Preview85候选包冻结后，在独立目录复制source1073的完整输入；应用/测试代码和全部断言原字节保持。仅SDK版本、隔离本地feed配置、runtime身份及相关锁更新。全新缓存普通/锁定恢复通过，Release/Debug完整solution各零警告/错误；21份锁的SDK版本/SHA512匹配冻结包，其他依赖版本/哈希未变。两配置WPF/Tests/CLI共六个输出各SDK13/native62逐文件匹配实际包/发布方清单，桥为`D7AF1A2118EE5C13AC789EAFDB85EE264009F291953C0987BC4E4B90255F9E02`。

[独立编译核验](../capability-evidence/diagnostics/public-nuget-migration/local85-consumer1073-verification.json)重新读取76个已编译DLL文件，221类型/696成员集合与原SDK31引用集合逐项相等，直接OCCT原生P/Invoke为0。这里证明实际源码可编译接入该本地SDK载荷；没有执行原生代码、应用全量或窗口，没有只从公开源恢复，没有确认发布或采用vcpkg载荷，生产仍SDK26。两个配置的本地编译不能替代这些剩余门禁。

上游旧provider的Release完整第二轮2255/2255通过，第一轮四个间歇性失败保留且根因未宣称修复；Debug完整2244/2255、11个真实窗口失败仍待诊断。固定vcpkg入口和overlay已写入上游，实际禁二进制缓存的新目录恢复正在进行；不得把旧provider结果用于新profile。
