# F5：成功修改回执的上下文保留

2026-10-08 接续：当前988保留此实现，contract只读示例/角色说明影响专项两配置各84通过，其中原回执28全部再执行；[当前影响审计](../artifacts/f5-contract-guide-focused-both-verification-1.json)。987原模型第一轮单图纸正常完成，第二轮view真实成功后错误contract耗尽16轮，23调用FAIL；[独立HTTP审计](../artifacts/f5-action-receipt-drawing-stall-verification-1.json)确认view回执在这些失败前持续逐字节保留，不能归因为该回执仍丢失。当前988原模型八轮正在复验，最终5737未运行；[角色反馈接续](F5_ACTION_FIELDS_AND_FEEDBACK_PLAN.md)记录当前边界。

2026-10-08 当前987已实现并通过Release/Debug专项各34/34（新增28、原上下文5和默认12工具守卫1），源码/测试载荷/SDK13及实际62库已独立核对；[相关两配置各932/932](../artifacts/f5-action-receipt-related-verification-1.json)已通过，完整5715尚未运行，原bonsai工程图八轮正在同16生产依赖上复验。[当前构建与清单](../artifacts/f5-action-receipt-build-2-exit.json)、[Release34](../artifacts/f5-action-receipt-release-focused34-2-verification.json)、[Debug34](../artifacts/f5-action-receipt-debug-focused34-2-verification.json)。相关实际932=原903+新28+此前PID ScratchFile检查1；首轮Release文件名related931只为准备标签，以清单/TRX实际数量为准。

实现从最新用户轮次的真实、完整工具交换识别最近一次success=true且changed/workspaceChanged=true的修改；同轮多工具的调用和结果一起保留，按原顺序优先裁剪其他交换。失败、no-op、发布文件但未修改文档、字符串布尔值、嵌套标志、重复字段、缺失结果或复用call ID均不作为修改证明。不改变原会话、指令、工具调用、图纸/文档状态或原16轮。

原6000/2400字符压缩继续生效。整个交换放不下时仍按原边界整组省略；在预算可容纳时增加独立System压缩元数据，明确最新成功交换已被省略、不得假设其ID或重复修改，应读取当前状态。这不是工具回执，也不替代对象身份。没有用户轮次时保持原路径；新用户轮次不永久固定旧修改。

首次专项30/34、4项失败保留：[首次失败](../artifacts/f5-action-receipt-release-focused34-1-verification.json)。失败只发生在新增的“长历史全部12工具”预期；冻结旧DLL与当前DLL逐项比较证明，这组原有长历史在普通/紧缩模式本来分别保留10/9工具且原版丢失成功回执，当前选中的工具完全相同并保留回执。[独立基准审计](../artifacts/f5-action-receipt-budget-baseline-verification-1.json)。只修正新增测试的基准，生产FitTools、原12工具目录及短历史12工具守卫、8192/输出/安全余量和所有旧测试未变；不为满足新断言扩大预算。

真实原生Box的实际图纸创建回执在12次生产原生来源查询后仍保留精确sheet ID；两模式图纸仍恰好一张，几何不变、撤销重做/零资产成立。它证明本地上下文行为，不能代替原bonsai八轮工程图验收；983两图纸FAIL及多文档八轮PASS继续保留。

2026-10-08：983原工程图第一轮创建两张图纸，原exactly-one断言FAIL。[实际失败](../artifacts/f5-native-context-lmstudio-drawing-1-verification.json)、[两次创建/回执范围](../artifacts/f5-native-context-drawing-duplicate-diagnostic-1.json)、[第二次创建前上下文审计](../artifacts/f5-native-context-duplicate-context-verification-2.json)。第二次创建前，第一次成功修改的真实回执/新图纸ID已从上下文移除；最新状态仍显示drawings=1，原用户“exactly one sheet”请求保持。该事实证明身份回执丢失，不能推断成重复创建的唯一原因。

下一阶段保留最新用户轮次内最近一次真正的成功修改及其完整工具交换，优先移除更旧的只读交换。只依据实际工具结果中success=true及changed/workspaceChanged=true识别修改；不由函数名字、用户文字、几何/哈希或看起来像成功的助手文字推定。所有工具调用与对应结果成组保留，不产生孤立结果。原会话不改，不伪造回执或用户消息。

原8192上下文、输出余量、6000/2400工具结果上限及16轮保持。不能容纳该完整交换时必须保留明确的压缩/缺失路径，不扩大预算或声称已保留；旧用户轮次不永久固定。此机制只改善上下文，不自动代替用户选择、重复执行/去重命令、把create改成edit或在原断言后删除多建的对象。

所需验证：真实Box/图纸修改ID在后续长只读交换后仍可见；正常/较紧预算与原12工具检查；多工具一轮无孤儿；失败和只读回复不冒充修改；新用户轮次、多个真实修改、无法容纳的交换、取消和上下文超限仍遵守原边界。实际模型仍沿用原提示/模型/16轮；工程图八轮须真正仅创建请求的对象、正常回答、保存重开和零资产才计通过。

接入前985的工作器Release/Debug各18、原640曲面检查两配置各640均已通过，[当前985构建](../artifacts/native-readiness-build-1-exit.json)、[Release640](../artifacts/native-readiness-release-640-1-verification.json)、[Debug640](../artifacts/native-readiness-debug-640-1-verification.json)保留。所有原20秒/原生几何/取消断言未变；983完整5684的5668通过/16失败仍保持原终态。当前987完整回归须覆盖这些原检查后才能称最终全量通过。A5、原生复制域四组和其余A–F继续，G暂缓。
