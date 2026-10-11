# F5：查询身份与 LM Studio 失败反馈接续

2026-10-08：接入前977完整Release/Debug各5565已通过。身份反馈已接入978并在两配置零警告/错误编译，新增16和相关回归正在运行；原模型八轮与当前完整5581尚未验。本页记录旧失败证据及实际接续范围，不是供应商验收通过声明。F5g已验本地作者契约、Codex八轮和LM Studio两次失败分别保留，A–F继续，G暂缓。

## 已核对的实际失败

`artifacts/f5-drawing-lmstudio-2/result.json`记录原八轮场景只完成第一轮，第二轮耗尽原16工具轮数。最后一份实际HTTP请求`wire-request-024.json`保存了连续三次调用：模型以`documentId`作为`cad_inspect area=drawings,id=...`的对象过滤器，收到`success=true,total=0,items=[]`后又重复相同查询；随后请求`drawing_views`仍使用该ID并缺少`sheetId`，收到`Missing or invalid sheetId`。这不是创建图纸已失败的证据，也不能以增加轮数或自动选择另一图纸来掩盖。

当前`CadAgentToolset`说明`drawing_views/dimensions`需要`sheetId`，但通用`id`字段缺少对象身份语义；`CadAgentDocumentTools`的`drawings`过滤允许错误角色的文档ID得到无解释的空集合。`cad_drawing`另有严格documentId/stateId/type/sheetId/id契约，生产写入不能因为模型混淆而放宽。

已只读查询原探针使用的本地`http://localhost:15630/v1/models`：返回`prism-ml/bonsai-27b`、`qwen/qwen3.8-27b`和嵌入模型。尚未发起新推理，也没有改用户供应商/模型/设置。服务可访问不能当作实际工具工作流通过；复验继续使用原`prism-ml/bonsai-27b`。

## 实施范围

1. 给`cad_inspect`和`cad_drawing`的身份字段加准确描述：documentId标识文档，stateId标识当前快照，sheetId来自实际图纸查询，id是所查询类型的对象ID。`model_contract`的id是类型名，保留这个既有特例，不把全部id强改成UUID。
2. 图纸查询为空且过滤器确实使用当前文档ID时，给出明确的角色不符反馈和只读发现步骤。先检查真实图纸对象是否存在，再判断角色误用：不同类型可以拥有相同GUID值，不能凭相等值错误拒绝实际存在的图纸。
3. 对缺失/错误sheetId的子对象查询，说明从`drawings`结果获取准确owner的方法。保留无目标、陈旧身份、未知类型、分页预算和严格写入守卫，不自动丢弃过滤器、选择第一个对象或执行修改。
4. 用合成文档验证正确查询、普通不存在对象、文档ID误用、真实GUID碰撞控制、缺失/错误owner、合法model_contract类型名，以及状态/作者/撤销和资产不变。生产命令、Agent和供应商schema展示分别检查，不能只用文本存在性测试代替行为验证。
5. 977完整回归终态和原生诊断16已收取；978查询修复已编译，按影响专项，再用当前生产DLL运行原八轮LM Studio工程图和多文档场景。保留原16轮上限、实际HTTP请求/工具回执、精确作者与原生输出、撤销/保存重开及零资产要求。失败仍按真实阶段报告，不换模型冒充修复。

## 验收边界

错误反馈改善不保证模型一定选择正确工具；必须看实际原流程是否成功。供应商仍须自己选择准确对象并调用严格生产入口。原F5g两次失败和多文档五次失败不覆盖；当前完整5565载荷保持，下一版生产修改及其对应验证单独冻结。

关联：[工程图工具及原供应商失败](F5_COMPLETE_DRAWING_TOOLS.md)、[多文档工具](F5_WORKSPACE_TOOLS.md)、[A–F跟踪](AF_IMPLEMENTATION_TRACKER.md)。

当前构建清单：[978/5581](../artifacts/f5-query-identities-build-2-exit.json)。首轮编译仅因新测试缺少Kernel.Abstractions命名空间失败，补using后两配置零警告/错误；原失败日志与回执保留，生产逻辑和原断言/16用例不改。

2026-10-08 接入结果：978两配置相关各788/788通过，新增16全通过。原模型工程图1轮/23调用和多文档5轮/34调用后仍超限；实际HTTP/工具/原生加载及零资产证据见[F5动作字段接续](F5_ACTION_FIELDS_AND_FEEDBACK_PLAN.md)。当前979动作反馈已编译但相关、新28和新模型流程未验；不把本地查询修复称为供应商八轮通过。
