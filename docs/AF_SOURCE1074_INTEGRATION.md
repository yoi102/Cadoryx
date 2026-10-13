# A–F source1074 / 公开 Preview.85 合并候选

后续状态：1074原提供方两场景已终态，工程图FAIL/多文档8轮PASS；1075两场景均FAIL。1076已修复显式空游标及过期只读恢复，组合两配置455专项通过并合入开发目录，详见[当前检查点](AF_SOURCE1076_DEVELOPMENT_INTEGRATION.md)。本页下方准备/运行中的描述保留当时状态，不作为当前进度。

2026-10-13。将已冻结的 source1073 完整候选与[F5默认分页配对](F5_DEFAULT_PAGE_QUERY_MATCHING.md)合并，原回归目录保持不变。1186份来源/工具输入中只覆盖 Agent 续页配对源码和正式禁止隐式 library-packs 源的构建属性，另加入一份16案例测试，合计1187份来源输入。原6619项测试源码保持；合并后预计6635项，尚未实际发现或运行全量，不把预计数算成执行数。

[独立构建与二进制消费核验](../capability-evidence/diagnostics/af1074-public85/verification.json)确认锁定恢复及完整solution的Release/Debug构建均零警告/错误。两配置直接消费本次完整候选生成的Agent/AI.Contracts二进制，各21/21（原上下文5、新分页16），不引用或加载OcctSharp。14个SDK包的版本及内容哈希与公开Preview.85父候选逐项相同，无新增封装或本地包切换。

[窗口载荷与原36场景](../capability-evidence/diagnostics/af1074-public85/window-matrix-preparation.json)已经准备：创建/编辑×中英日×六个精确夹具，确认/取消/关闭路线与原六状态、30/300秒条件保持。两配置观察器源码与已修正模态顺序的第五版逐字节相同，应用托管DLL、语言资源和公开native62磁盘载荷匹配。实际桌面未开始；开始前提示用户，等待父候选两配置终态/审计、SDK诊断结束和原生消费者退出，只操作本任务的窗口。

[原提供方探针](../capability-evidence/diagnostics/af1074-public85/provider-build-verification.json)已用当前候选编译，原三份C#提示/流程/断言逐字节保持；16份共享应用、13份SDK和62份原生文件匹配。默认1234地址拒绝连接后，实际CLI显示服务运行于15630；该服务列出原`prism-ml/bonsai-27b`，本机单一缓存变体为Q1_0，发现时没有已加载模型。没有更改服务设置或发送工作流请求。实际验收仍使用原模型、8192上下文、16工具轮、工程图/多文档各八轮及完整原断言，原失败保持。

2026-10-13运行增量：父候选[完整6619两配置](../capability-evidence/diagnostics/public-nuget-migration/public85-final1073-full-both-verification.json)均已终态，各6595通过/24原严格STEP失败/零跳过；原6556及新增63完整保留，无新增失败，原十二工具恢复预算失败两配置均已通过，实际native62准确。SDK原版对照两配置原生也已结束，正在整理分段误差/模块来源报告，未取得修复。原Bonsai现已在实际15630服务重新加载Q1_0、8192上下文；工程图工作流开始真实运行，之后串行多文档，尚无终态。此前“未加载/未执行”是准备检查点。

本候选尚未合入生产，完整6635、实际窗口/材质/性能和提供方门禁均未通过。SDK误差分段诊断不能代替原24项通过。A–F继续；G和Cadoryx清理暂缓。
