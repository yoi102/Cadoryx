# F5：动作字段反馈与原供应商接续

2026-10-13：[1077隔离动作字段说明](../capability-evidence/diagnostics/af1077-drawing-action/verification.json)只改 `action.description` 为 `contract:action/type only. Else IDs required (inspect_sources too).`，原、新均67字符，原始及实际wire schema均1326字符。完整1189来源核验只差这一处，原6652测试源码、解析/身份守卫、默认12工具、上下文算法及原Bonsai三份来源保持。实际两配置ViewModels构建零警告/错误，原编译测试体的45项断言逐配置直接调用全部通过，含原激进恢复预算和长读取回执；这不是完整xunit/原生回归。验收程序已编译，SDK13/native62磁盘载荷匹配公开85。当前生产仍1076，[真实进程队列](../capability-evidence/diagnostics/af1077-drawing-action/queue-verification.json)等待生产根两配置6652终态、无新增失败及原生空闲后，再执行原drawing/workspace场景；模型尚未运行，不能宣布工作流修复。第一版队列在原生执行前发现缺少两个元数据路径，仅停止核对身份的自建等待进程，第二版重新排队，原全量作业保持。

以下988及更早为历史检查点；当前实现和验收状态按上方1077及[STATUS](STATUS.md)阅读。

2026-10-08 当前988已接入contract只读请求示例和前置角色说明，两配置零警告/错误，5737清单保留原5715+新增22；Release/Debug影响专项各84/84通过（新示例22、原动作反馈28、成功回执28、原上下文及默认工具6）。[当前构建](../artifacts/f5-contract-guide-build-1-exit.json)、[两配置84](../artifacts/f5-contract-guide-focused-both-verification-1.json)。完整5737未运行，原bonsai工程图八轮正在同16生产依赖/SDK13/实际62载荷上复验。A–F继续，G暂缓。

## 当前contract角色反馈

987真实工程图第一轮正常完成且恰好一张图纸；第二轮实际Top视图创建成功后，模型反复把documentId/stateId/sheetId发送到contract，最终耗尽原16轮，共23次工具调用。[实际FAIL](../artifacts/f5-action-receipt-lmstudio-drawing-1-verification.json)、[失败时上下文独立核对](../artifacts/f5-action-receipt-drawing-stall-verification-1.json)。成功view回执在这些失败请求之前始终逐字节保留，实际schema也已含action/type说明；不能继续归因为回执被裁掉，也不能据本次单样本认定唯一原因或模型可靠性已验。

当前保持原字段拒绝、success=false和未执行状态，仅在错误contract回复中附加既有只读validRequests。调用者明确提供合法且不重复的type时，只给该type的合法请求；缺失、未知、非字符串或重复type时列出原12种已有请求，由调用者选择，不代替对象/类型选择。所有示例只含action/type，不包含文档、状态、owner或原生来源身份。示例不会被工具自动执行，不将create改成edit，也不删除不受欢迎的作者。

例如调用者明确给出type=view却夹带身份时，仍拒绝原请求，独立示例为`{"action":"contract","type":"view"}`。原12类示例逐项实际调用均取得原生产contract；真实Box的一张图纸/一个Top视图在拒绝与显式只读查询后保持原快照、几何、历史及资产数，撤销重做/关闭零资产仍成立。普通/紧缩8192、输出与安全余量、全部默认12工具及前述28回执检查保持。

工具完整schema（包括嵌套description、类型、枚举顺序、required和所有预算）与987冻结DLL逐字节相同，[schema独立审计](../artifacts/f5-contract-guide-focused-both-verification-1.json)。只调整顶层cad_drawing说明，先说明contract不接受ID，并减少重复文字；未改变原模型/端点、用户与基准系统提示、原16轮或验收断言。当前新载荷供应商结果仍须单独收取；987与更早的FAIL原样保留。

以下为979实施时的历史过程，不能代替当前988的终态。

## 实际失败与范围

原模型仍是`prism-ml/bonsai-27b`，实际端点`http://localhost:15630/v1`，未更换模型、提示、16工具轮数或原几何断言。两次均核对当前生产依赖16程序集、SDK13和实际加载62库，关闭后资产归零。控制台探针不引用WPF视口程序集`Cadoryx.Rendering.Occt`；最初按测试端17程序集检查因此失败，按真实deps依赖集合独立重审通过，原失败及源码保留，未替换生产DLL。

- 工程图完成第一轮，共23次生产工具调用；第二轮在实际视图创建成功后仍未正常结束，耗尽16工具轮数。实际错误包括`contract`夹带身份/owner字段、缺失documentId后再缺失stateId，以及create把name放在外层。没有完成八轮、关联尺寸/孔表/保存/PDF的供应商证明。[实际HTTP与工具审计](../artifacts/f5-query-identities-lmstudio-drawing-1-verification.json)。
- 多文档完成前五轮，共34次调用；第六轮实际open已有文档成功，随后模型进行不需要的历史操作，cursor缺少partId却只收到没有路径的错误；改在根层后又被正确拒绝，最终耗尽16轮。不能把一次open成功当作整链通过。[实际审计](../artifacts/f5-query-identities-lmstudio-workspace-1-verification.json)。

查询身份反馈本身已有本地行为证据；上述失败属于进一步的动作字段、嵌套字段路径和模型结束行为问题。原旧五次多文档/两次工程图失败及这两次新失败均保留。

## 已接入979的动作反馈

生产解析和错误说明复用同一份`CadDrawingAuthoring.AllowedRequestFields`动作字段表，保持每个动作原有可接受字段。`contract`只接受action/type；其他工具动作仍要求精确documentId/stateId。外层name只用于save_template，创建/编辑的名字属于authorJson。

错误回复在执行前同时列出缺失或无效的两种文档身份、未知/重复字段和该动作的允许字段；不填入、删除或替换调用者字段，也不选择对象或修改作者。未知字段说明最多16条，名称最多80字符加省略号；原131072字输入上限和解析器、状态/关闭/只读、原生选择、owner/type、作者严格解码及一次事务守卫保持。最终schema仅保留action/name两条字段说明，身份要求与工具说明共用既有表达；类型、枚举、required、数值/长度预算保持。

新增28项待验证检查覆盖13个非contract动作的身份对反馈、contract额外字段、两个身份加错误外层name、无效GUID、重复身份、模板name例外、实际外来文档/陈旧状态、合法sheet一次提交/撤销及有界错误长度。必须保留默认8192上下文的全部12工具检查、旧查询16、原工程图169及其余必要相关行为；编译和发现清单不计作测试通过。

当前：[构建/5609清单](../artifacts/f5-drawing-action-feedback-build-1-exit.json)、[接入前查询788两配置](../artifacts/f5-query-identities-related-verification-1.json)、[实际接入记录](../artifacts/f5-drawing-action-feedback-apply-1.json)。

首轮979 Release相关816完整结束为815通过/1失败/0跳过，新增28全部通过；失败是较紧8192上下文中重复说明使cad_datum被挤出，原12工具检查正确阻止了验收。仅移除重复的documentId/stateId两条说明，预算、工具数、解析规则和原断言不改。修正后两配置零警告/错误编译，Release针对性46全部通过，包含新28、查询16以及普通/较紧上下文的全部12工具检查；同一修正载荷的Release/Debug816第二轮正在串行运行，尚无完整两配置终态。[原815/1](../artifacts/f5-drawing-action-feedback-release-related-1-exit.json)、[说明压缩记录](../artifacts/f5-action-feedback-context-fix-1.json)、[修正构建](../artifacts/f5-drawing-action-feedback-build-2-exit.json)、[46回执](../artifacts/f5-drawing-action-feedback-release-context46-2-exit.json)。

## 下一批按依赖顺序推进

当前983实际终态已收取：原多文档八轮PASS，30调用/0工具错误、零资产；工程图第一轮重复创建两张图纸仍FAIL，未到选择分页。两配置903已验，完整Release5684正在运行。工作区角色/动作字段反馈仍未实施，原历史失败保持，不以单次八轮PASS声称重复可靠性。工程图回执保留范围与后续创建语义须继续核对，不能通过删除多建图纸或改原断言获得PASS。[多文档PASS](../artifacts/f5-native-context-lmstudio-workspace-1-verification.json)、[重复创建诊断](../artifacts/f5-native-context-drawing-duplicate-diagnostic-1.json)。
当前983两配置相关已各903通过，零失败/跳过；同载荷的原模型工程图八轮已启动，尚无终态。新的工作区角色反馈仍未接入，严格expected/target不替换。见[903审计](../artifacts/f5-native-context-related-verification-1.json)、[生产载荷](../artifacts/f5-native-context-provider-build-verification-2.json)。下方运行中状态为此前记录。
当前983接续：历史cursor路径已接入并新增28项；原生选择回执上下文分页已接入并新增26项，两配置零警告/错误构建，Release合计55检查通过，当前两配置相关正在运行。980原工程图2轮、多文档7轮后的FAIL保持；当前983原模型尚未运行。后续工作区动作字段/expected角色反馈仍未接入，不自动替换身份或扩大预算。见[历史cursor实现](F5_HISTORY_CURSOR_FEEDBACK.md)、[上下文选择分页](F5_NATIVE_SELECTION_CONTEXT_PLAN.md)。下方979/980规划及草稿记录为历史。
2026-10-08接续：979两配置各816已验，980有界空回复恢复新增21、两配置相关各837通过；原模型工程图新生产载荷正在复跑，尚无供应商终态。下列cursor步骤仍为草稿，多文档动作反馈未接入；不将本地契约计作八轮供应商通过。见[当前恢复实现](F5_BOUNDED_EMPTY_RESPONSE_RECOVERY_PLAN.md)。

1. 完成979相关回归，先诊断原失败，不扩大上下文、轮数或几何预算。用新生产依赖发布独立探针，核对schema结构除说明以外与原实际HTTP结构一致，再运行原模型及原两种八轮场景。
2. 对历史工具补准确的`cursor.partId/stateId/position`路径反馈，区分根层partId与cursor内字段；不把缺失cursor推断成null，也不生成新的历史游标。补缺失/错误/重复路径、陈旧游标、返回末端、撤销、只读和状态不变的行为检查。
3. 工作区的list/new/open/activate动作继续使用严格字段与完整expected/target；根据实际错误补动作说明和一轮完整反馈，避免模型在list夹带path或错误角色ID。沿用实际用户切换取消、原子提交及原8轮断言。
4. 核对实际上下文和返回的工具消息是否保留当前身份、已成功动作和最新用户请求。模型未正常结束或超限保持明确失败，不自动认定任务完成；任何筛选或恢复机制须另立契约，不隐藏需要的工具或扩大预算。

原生身份历史的输入复制、最终结果和BRep往返桥接继续按[B18原生历史规划](B18_GUIDED_HISTORY_PROVENANCE_PLAN.md)推进；本页不构成其证明。A5性能/材质、其余草图/实体/制造/外部交换待办仍见[A–F跟踪](AF_IMPLEMENTATION_TRACKER.md)。

2026-10-08 第二轮终态：修正后979 Release/Debug相关各816/816通过，零失败/跳过，原788、查询16和新28全部保留，全部12工具及原预算通过；SDK13和实际62库绑定。独立生产探针的真实16依赖与当前测试载荷匹配，原工程图八轮正在运行，未有新供应商PASS。[两配置816](../artifacts/f5-drawing-action-feedback-related-verification-2.json)、[生产探针](../artifacts/f5-drawing-action-feedback-provider-build-verification-2.json)。历史cursor反馈草稿已准备，未应用/编译/运行，不计通过数。

当前979原模型复验终态仍FAIL：第一轮通过、22工具调用，第二轮实际create view成功后最终空回复/无工具，未完成原八轮。实际所有HTTP schema除说明文字外与978一致，生产16依赖/SDK13/实际62库及零资产核对通过；有界空回复恢复尚待实现，不扩大16轮或伪造最终文字。[实际新FAIL](../artifacts/f5-drawing-action-feedback-lmstudio-drawing-1-verification.json)、[空回复恢复规划](F5_BOUNDED_EMPTY_RESPONSE_RECOVERY_PLAN.md)。
