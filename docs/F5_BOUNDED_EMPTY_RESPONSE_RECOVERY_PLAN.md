# F5：有界空回复恢复实现与验收

当前983真实原bonsai多文档八轮已通过独立审计：30工具调用、零工具错误、保存重开/切换/零资产，原16轮、模型、端点及提示保持。此为单次八轮通过，旧980第8轮再次空回复FAIL和真实恢复事件不改。[当前八轮PASS](../artifacts/f5-native-context-lmstudio-workspace-1-verification.json)。当前工程图第一轮重复创建两张图纸仍FAIL，未到原生选择分页；本地903/55保持，当前完整Release5684运行中。

980原模型复跑终态：工程图完成2轮/34工具调用，第三轮耗尽原16轮，没有空回复恢复事件；五次原生选择页在上下文截断导致CylinderFace丢失，见[选择分页诊断](F5_NATIVE_SELECTION_CONTEXT_PLAN.md)。多文档完成7轮/46调用，第8轮后来再次空回复而FAIL；真实第6/7/8轮均触发一次恢复，并返回真实文字/工具，第6/7完成，第8恢复后仍再次空回复，正确停止且没有第二次恢复。不是相邻两条空回复。所有依赖/实际62原生及零资产核对，原八轮门禁保持。[工程图实际FAIL](../artifacts/f5-empty-retry-lmstudio-drawing-1-verification.json)、[多文档实际FAIL](../artifacts/f5-empty-retry-lmstudio-workspace-1-verification.json)、[真实恢复请求/回复审计](../artifacts/f5-empty-retry-actual-recovery-verification-1.json)。

当前983已接入[历史cursor反馈](F5_HISTORY_CURSOR_FEEDBACK.md)与[原生选择上下文分页](F5_NATIVE_SELECTION_CONTEXT_PLAN.md)，Release55项通过，两配置相关正在运行；旧980的837是此前冻结，新983原模型八轮尚未运行。原生身份组合、A5及其余A–F继续，G暂缓。

## 980实现与本地验收（历史冻结）
2026-10-08 当前980已实现恢复，本地检查已通过；原LM Studio八轮正在复跑，供应商验收尚无终态。下方979失败与最初规划作为历史保留。

仅当真实回复没有文字、没有工具调用且还有原逻辑轮次时，最多允许一次恢复。实际空回复保留在会话中；下一次请求临时增加系统说明“Empty reply: use receipts; finish latest request; do not repeat completed edits.”，随后移除。没有伪造用户消息、工具回执、最终答案或生产命令；原16工具轮数及输出/上下文预算不扩张。

二次空回复仍返回ResponseWasEmpty=true；单轮或末轮不恢复，末轮恢复工具调用仍执行正常严格工具路径并保留原超限失败。循环开始和每次工具执行前检查取消；供应商异常不混同空回复，上下文超限重试保持为独立事件。最新用户内容与附件继续作为数据，未由恢复说明替换。

新增EmptyResponseRetry枚举追加在末尾，原数值不变。窗口按现有会话代次守卫处理三语言恢复文字，以System角色记录；真实助手内容仍为Assistant。1365个语言键完整对齐，新强类型成员来自实际ResGen生成结果，原成员保持。

新增21个检查覆盖正常回复、null/空白恢复、连续空回复、轮数边界、取消、真正的供应商异常、临时说明、上下文超限事件、短/长会话、最新用户及附件内容和三语言实际Toolbox状态。真实原生Box24mm³只提交一次，回执穿过空回复恢复、可撤销且资产归零；普通及较紧8192上下文均保留全部12工具。

Release专项23/23、Release与Debug相关各837/837通过，零失败/跳过；原816、新21逐项保留。两配置构建零警告/错误，清单5630=原5609+新21。首轮仅测试fake接口方法名编译失败，修正为ResetConversationAsync；原失败保留，生产与行为断言未改。SDK22、ABI1.81、桥0.89.0和solver17保持。

证据：[应用构建/清单](../artifacts/f5-empty-retry-build-2-exit.json)、[Release专项23](../artifacts/f5-empty-retry-release-focused23-1-exit.json)、[两配置837独立审计](../artifacts/f5-empty-retry-related-verification-2.json)、[相关协调终态](../artifacts/f5-empty-retry-related-both-2-exit.json)、[生成成员](../artifacts/f5-empty-retry-generated-1/generated-member-verification.json)。当前完整5630和通用WPF/材质/性能未运行；977完整两配置5565属于此前冻结。

当前独立生产探针16个实际依赖、SDK13/native62与已测试载荷匹配。原端点http://localhost:15630/v1、prism-ml/bonsai-27b、用户/基准系统提示、16轮及原八轮断言保持。实际HTTP须核对schema除description以外保持原结构，并留存空回复、恢复请求、实际工具和原生加载；全部八轮、撤销/保存重开及零资产才能计为供应商流程通过。局部创建视图成功或服务可访问不能计作通过。[生产载荷审计](../artifacts/f5-empty-retry-provider-build-verification-2.json)。当前工程图已启动，多文档新载荷尚未运行。历史cursor路径反馈仍为未应用/未编译/未运行草稿；原生身份组合、A5及其余A–F继续，G暂缓。

## 979失败与最初规划（历史）

2026-10-08：979原LM Studio工程图第二轮成功创建Top视图，随后反复查询，最后返回content为空、ToolCalls为0的回复。原八轮仍失败；实际schema除说明文字外与978逐字段一致，两配置相关各816已通过。此文件是下一阶段规划，不是已实现或已验收的恢复能力。

依据：[当前实际原模型失败](../artifacts/f5-drawing-action-feedback-lmstudio-drawing-1-verification.json)、[动作字段实现与失败保留](F5_ACTION_FIELDS_AND_FEEDBACK_PLAN.md)、[生产AgentRunner](../Cadoryx.Agent/AgentRunner.cs)。当前Runner遇到没有文字、没有工具的回复立即返回ResponseWasEmpty=true，界面与原探针正确保留失败。

下一步只在原工具轮数内，允许一次这种空回复的恢复；不替换模型、增加工具轮数或输出/上下文预算，不把已成功的局部动作称为整个请求完成。恢复须基于实际对话中已有的工具回执，要求模型继续最新用户请求或给出事实回答，并明确不要重复已完成的修改。修复可使用单次临时系统说明，不能伪造用户消息或工具结果；原探针的用户提示与基准系统提示保持。实际空回复和恢复请求都要留存。

必须单独报告恢复事件，不能冒充普通助手回答或上下文超限事件；窗口需按现有三语言资源约定处理。一次恢复仍为空、取消、原轮数已经耗尽或发生真正的供应商异常时，继续明确失败，不自动重放生产命令。原模型最终确有非空回复、严格工具回执、全部八轮、撤销/保存重开及零资产后才能计为该供应商流程通过。

所需行为检查：正常回复不多请求；首次空回复后实际回答；连续两次空回复停止；单轮/末轮边界不扩大；已提交工具回执保留且修改不重复；取消不再请求/写入；真实供应商异常不混同空回复；恢复系统说明不改变最新用户内容、不接受附件指令；短/长上下文及普通/较紧8192原12工具守卫仍保持。模拟供应商检查和原LM Studio实际八轮分列。

先冻结/完成本轮979证据，再实现、编译和按影响验证，保留当前失败。[历史cursor路径反馈](F5_ACTION_FIELDS_AND_FEEDBACK_PLAN.md)独立跟踪。原生完整身份组合、A5及其余A–F继续，G暂缓。
