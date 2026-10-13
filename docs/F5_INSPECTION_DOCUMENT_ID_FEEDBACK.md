# 检查工具的文档与对象身份反馈

2026-10-14。已合入当前开发代码，继续消费公开 OcctSharp Preview.85。
D7/本地87.cu1仍为隔离候选；A–F未全部完成，G和清理暂缓。

原工作区模型在同一文档/状态下，无过滤的bodies/parts各有1项，后来把
documentId填进对象过滤id，却得到成功的空页。生产此前仅为工程图解释了
这个身份误用，其他检查领域没有提示。原模型最后超过16工具轮；这个现象
已确认，但不能据此认定它是工作流失败的唯一原因。

现在18个对象过滤领域在原检查成功之后，拒绝没有实际对应对象的documentId
过滤，并返回未执行的只读查询建议。八个所有者过滤领域同样解释误用；
parameter_targets的partId实际过滤ownerDefinitionId，允许零件或装配所有者。
没有选对象、填身份、执行建议或自动重试，原12工具及schema保持。

普通未知对象仍返回原空页；实际对象与文档合法共用GUID时继续可查询。
合法身份被分页、其他零件范围、空选择或没有数值参数隐藏时也保留空页。
关闭请求和已关闭会话在返回对象信息/引导之前拒绝；取消、缺失上下文、
无效/重复/未知字段和分页守卫保持。

| 验证 | Release | Debug |
| --- | --- | --- |
| 原公开85视图模型，同一185断言 | 139通过 / 46问题复现 | 139通过 / 46问题复现 |
| 修正后当前开发，同一185断言 | 185通过 / 零失败 | 185通过 / 零失败 |
| Tests依赖图、最终WPF编译 | 零警告/错误 | 零警告/错误 |

185包括126新增及59原有相关断言，直接调用真实编译代码；不是完整xUnit
或真实模型验收。新增夹具仅使用明确的元数据和禁止几何调用的kernel，
没有把这些不含原生BRep的元数据当成几何正确性证据。实际OCCT加载为0，
文档/显示状态、generation、撤销重做、资产、选择与几何调用保持。
最终WPF的ViewModels与已测试载荷相同，SDK13/native62磁盘匹配公开85。

证据：[开发根核验](../capability-evidence/diagnostics/inspection-document-id/root-verification.json)、
[隔离候选185核验](../capability-evidence/diagnostics/inspection-document-id/verification2.json)、
[原模型诊断](../capability-evidence/diagnostics/af1081-local86/workspace-document-filter-feedback-diagnostic.json)。
早期155/184验证、初轮非法参数名称夹具、assembly分页夹具误读JSON路径和
一次LF/CRLF归档误判均保留。parameter_targets的查询建议断言依据原有
ownerDefinitionId契约从parts改为parameter_targets，没有排除该领域。

当前根预计6973尚未完整发现/运行；下一组合含D7预计7117，也未运行。
原1082+D7/87.cu1冻结6991的Release已实际6967通过/原24精确延期失败/零跳过，
Debug继续串行运行。它们不能代替包含本修正的最新组合验收；原Bonsai两场景、
36 D6窗口、24/36恢复、材质实际UI和MiniPC性能仍待。正式已验子批仍
A4/B17/C5/D5/E4/F5g。
