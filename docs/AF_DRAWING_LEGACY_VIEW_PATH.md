# 工程图旧入口的路径反馈

2026-10-14，开发源码1082，公开 OcctSharp Preview.85；本地86仍是隔离候选。

原 Bonsai 工程图工作流在第二轮反复调用受支持的
`cad_edit action=add_drawing_view`，省略 `path` 或填入字符串 `"\"\""`。
生产工具正确拒绝，但只返回缺少路径或路径格式错误，没有说明从哪里读取
完整实例路径。实际工作流没有完成要求的 `cad_drawing.create` 收据，仍为失败。
该旧入口不是未知 action，正常的精确来源调用必须继续可用。

在原活动文档、可编辑状态、documentId/stateId 和 sheetId 检查之后，
无效路径现在返回明确的 `drawing_view_path_invalid`，以及两个只读建议：

- `cad_inspect area=occurrences`：读取完整 `items[].path`，不误用文档或末级槽 ID。
- `cad_drawing action=contract,type=view`：选择该入口时，先读取真实 author 契约。

返回建议不执行读取、不挑选对象、不填入身份、不自动重试。合法旧路径仍走
原创建流程；其他编辑 action、原12工具及 schema 保持。

| 验证 | Release | Debug |
| --- | --- | --- |
| 新17项对原行为 | 7通过 / 10缺少反馈失败 | 7通过 / 10缺少反馈失败 |
| 修正后新17加原32 | 49通过 / 0失败 / 0跳过 | 49通过 / 0失败 / 0跳过 |
| Tests依赖图和WPF编译 | 零警告/错误 | 零警告/错误 |

原32包含真实原生视图创建；实际加载的62个原生DLL路径/哈希匹配已验证的公开85。
最终WPF的ViewModels程序集与已测试载荷逐字节一致。新增拒绝夹具还核对文档、
撤销重做、资产和几何调用保持，以及身份、图纸、关闭、脱离、取消和合法路径守卫。

证据：[聚焦49](../capability-evidence/diagnostics/af1082-drawing-legacy-path/verification.json)、
[最终WPF编译](../capability-evidence/diagnostics/af1082-drawing-legacy-path/application-build-verification.json)、
[原1081/86模型失败独立审计](../capability-evidence/diagnostics/af1081-local86/provider-verification.json)。
两个模型场景分别只完成1/8及5/8轮；提示、8192上下文、16工具轮和每轮4分钟未改，
原始1193来源、137载荷及实际native62已核对。工作区失败为超过工具轮预算。

此次没有重新运行真实模型或当前预计6847全量，不代表F或A–F完成。
原24严格STEP失败按[用户授权精确延期](../capability-evidence/diagnostics/af1081-local86/step24-user-deferral.json)，
保持失败与后续修复责任；其他失败没有被延期。
