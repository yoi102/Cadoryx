# F5 控制标记回复的处理

2026-10-11。此项继续 A–F 命令与 AI 工作，G 实机、安装与交付暂缓。

## 实际失败和原因

保留的 bonsai-27b 工程图八轮验收在第三轮失败：成功读取来源后出现一次空回复，随后返回的文本只有 `assistant` 和完整的空 `<think>` 块。当前 Agent 仅用字符串是否空白判断答复，将这段控制标记作为 AssistantMessage 和非空最终答复，实际请求的 `cad_drawing.create` 没有成功生产回执。见[原模型失败](../artifacts/f5-direct-af-identity-reinspection-draft-1/lmstudio-drawing-2-verification.json)和[原事件](../artifacts/f5-direct-af-identity-reinspection-draft-1/lmstudio-drawing-2/events.json)。

该记录证明了控制标记回复分类的问题，不能据此解释多文档的全部失败。多文档第七轮仍有缺失/错误 expected 身份、读取后重复使用失效身份和达到原 16 轮上限的问题；原失败保持。

## 修复范围

候选仅在 Agent 判断是否存在可见答复时识别完整控制帧：单独的 `assistant`，或者可选的独立 `assistant` 行后只有完整 `<think>...</think>` 块。实际答复、代码块、正文中的字面标记和不完整标记保持原行为，不改写内容。原始 Assistant 内容仍完整留在会话历史中。

控制帧不显示为用户答复，也不能冒充最终回答；已有工具调用继续执行并保留真实回执。仍只允许原一次空回复恢复，仍计入原工具轮次；已经用过恢复或没有剩余轮次时返回真实空回复状态。不会填入身份、重放已完成编辑、更换模型或扩大上下文、输出与工具预算。

见[修复来源与实际失败绑定](../artifacts/f5-control-only-response-draft-1/preparation-2.json)和[源码](../artifacts/f5-control-only-response-draft-1/source-3/Cadoryx.Agent/AgentRunner.cs)。实际 Agent 消费程序 Release、Debug 各34项已独立通过，尚未接入生产；见[两配置实际验证](../artifacts/f5-control-only-response-draft-1/consumer-both-3-verification.json)。

## 已准备的验证与执行顺序

1. 独立消费程序编译实际 Agent 实现，复用完整原 21 项空回复恢复源码，新增 13 项：原控制帧/换行/推理帧、实际与字面文本、原一次恢复与最后轮次、控制帧同时包含工具调用时的回执保持。预期 Release、Debug 各 34 项，保留原断言。见[消费程序冻结](../artifacts/f5-control-only-response-draft-1/consumer-preparation-1.json)。
2. 已实际编译、运行并独立核对两配置各34项逐名 TRX、原21完整保留、载荷及实际62个SDK26原生库。首轮新增分类方法缺少非空结果的流分析契约，报CS8602；新副本添加精确的NotNullWhen(false)，不禁用警告。第二轮WPF消费项目缺少原测试项目隐式提供的System.Net.Http导入；只在新夹具项目显式补充，原测试源码不改。两次失败及源码保留，第三消费版本全部通过。
3. 本地行为通过后合入统一候选，并验证受影响的 Agent、工具预算、UI 与原生产工具行为。继续原模型、原提示、原上下文/轮次的工程图与多文档真实验收。

34 项本地行为通过不代表真实供应商工作流成功。旧工程图两轮/20 调用和多文档六轮/41 调用失败保持，A–F 尚未完成。

2026-10-11 status177：已验源码及13新增场景整合到[SDK27/材质/F5统一来源](../artifacts/a5-sdk27-control-unified-draft-1/preparation-1.json)。预计6122项（原6109完整保留）；源码准备不代表统一应用运行或原bonsai工作流通过。
