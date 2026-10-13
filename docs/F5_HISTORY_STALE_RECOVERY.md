# F5 历史游标显式空值与拒绝后的重新读取

2026-10-13。原 Bonsai 的 [1075 多文档工作流](../capability-evidence/diagnostics/af1075-public85/provider-runtime-verification.json)完成五轮，第六轮超出原16工具轮。记录中两次 `return_end` 使用了不匹配的游标，收到通用过期错误后继续读取不含历史游标的 `parts` / `sketches`，随后重复提交。两场景都没有触发新增的 `completeSourceItems`，不能归因该功能或宣称模型已修复。

进一步实际测试复现：作者检查使用 `WhenWritingNull`，在真实历史末端省略了根级 `cursor:null`。历史工具却要求明确传入检查到的游标，包括历史末端的显式空值。此次修复只将该根字段写为 JSON 值，保留其他可选空字段的既有规则；回滚时仍返回完整真实游标。

历史工具的 `StaleDocumentException` 回执现在附带只读恢复查询：`cad_inspect`，参数为 `{"area":"recipes","offset":0,"limit":1}`。该检查返回根级 `documentId`、`stateId` 和真实 `cursor`，提示明确复制、不要把 `items[].position` 当作当前预览游标，并在重试前核对原操作及当前特征版本。拒绝过程不编辑、不替换期望、不猜测空游标、不自动重绑。

[生产专项证据](../capability-evidence/diagnostics/f5-history-stale-recovery/verification.json)：Release / Debug 各261通过，零失败/跳过。新增四种实际原生场景覆盖历史末端凭空构造游标、同一作者状态下预览位置变化、作者状态变化和其他文档身份；拒绝与重新读取都保持作者/显示快照、游标、generation、历史、资产和脏状态，显式读取后才允许新的请求。初次测试的访问控制编译错误、三个省略空游标失败和一个两特征夹具错误均保留，没有放宽断言。

[完整1076组合候选](../capability-evidence/diagnostics/af1076-public85/verification.json)两配置完整solution构建零警告/错误，实际发现6652；[用例清单与补充线协议检查](../capability-evidence/diagnostics/af1076-public85/inventory-and-wire-verification.json)确认原6619名称全部保留、新增33。每配置436项实际原生专项加19项独立分页/线协议检查，共455个不同用例通过；实际native62路径与哈希匹配公开Preview.85，原激进十二工具恢复预算仍通过。

已将完整组合源码[合入开发目录并编译/发现](../capability-evidence/diagnostics/af1076-public85/production-root-verification.json)，1189份来源匹配候选。合入后生产根还没有运行完整6652或重新执行本轮原生专项；原模型未复验。本轮测试证明回执契约和拒绝恢复路径，不能证明原八轮提供方工作流可用。原提示、模型、8192上下文、6000/2400字符、16工具轮和几何门禁保持。A–F继续，G及清理暂缓。
