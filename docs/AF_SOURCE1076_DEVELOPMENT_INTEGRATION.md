# A–F source1076 开发合入检查点

2026-10-13。完整组合源码已写入生产开发工作树，[合入记录](../capability-evidence/diagnostics/af1076-public85/development-source-integration.json)保存55个现有更改、44个新文件及合入前备份身份；1189份来源/工具输入逐文件匹配冻结候选。配置仍使用公开 `8.0.1-preview.85`，没有新增本地SDK包。以后确需新增OcctSharp API时继续遵循用户允许的本地精确版本NuGet开发方式，并从生成器实现。

合入范围包含此前待合入的材质持久化与恢复呈现、视口故障处理、混合草图精确作者求解、删除面历史及持久化、有限曲面独立新实体和自由起始平面、AI作者身份/来源路径恢复、完整原生条目上下文，以及本轮的显式空游标/历史过期只读恢复。原C/E等已有生产能力保持；本次不代表这几个大批已完成。

[组合候选编译与实际原生专项](../capability-evidence/diagnostics/af1076-public85/verification.json)完成两配置完整solution编译，零警告/错误；每配置436通过，实际native62路径/哈希准确。[补充线协议与名称清单](../capability-evidence/diagnostics/af1076-public85/inventory-and-wire-verification.json)两配置各19通过，与436不重叠，共455项；实际发现6652，原6619全部保留，新增33为分页16、完整条目13、历史恢复4，原断言/预算保持。原激进十二工具恢复预算两配置仍通过。

[合入后生产根核验](../capability-evidence/diagnostics/af1076-public85/production-root-verification.json)确认锁定恢复、完整solution两配置编译及各6652发现成功；生产根与受测候选来源一致，两个WPF/两个Tests输出各13个托管SDK与62个原生文件匹配公开父载荷。`global.json`格式不同但解析值完全相同，稳定SDK10.0.401；首次合入收据的字节等同性断言失败已记录，没有重新复制或篡改SDK选择。

合入后生产根[Release完整6652](../capability-evidence/diagnostics/af1076-root-full/release-verification.json)已实际执行并核验：6628通过/原24严格STEP失败/零跳过，没有新增失败，原6619及新33保持，实际native62准确。Debug在原协调的空闲检查退出后只续跑缺失配置，仍待终态；455属于冻结候选此前专项证据。父1073两配置6595/24不能代替1076两配置结果。更早本地专项、工程图/窗口/性能证明仍按各自来源区分。

当前未关闭的门禁：

- B18 / F交换：原24严格STEP拒绝保持。精确mapped-scale两个最小方案[独立接收核验](../capability-evidence/diagnostics/exact-step-mapped-scale/receipt-verification.json)均strict BRep FAIL，未采用、未修改生成器或公共包；上游继续定位具体BRep状态及坐标系。
- D6：原36窗口矩阵保持。ResourceAssembly与缺失工作进程两项宿主启动问题已修复；[第三版实际首例](../capability-evidence/diagnostics/af1074-public85/window-startup-and-trace-verification.json)打开生产模态窗口后取消，六状态轨迹未完成，不能计通过。辅助源码已保存在 `tools/CadFiniteSurfaceWindowProbe`，没有为自动化修改生产窗口。
- F5提供方：[1076原两场景](../capability-evidence/diagnostics/af1076-public85/provider-runtime-verification.json)实际多文档8轮PASS/资产0，工程图第三轮缺尺寸创建收据FAIL；完整柱面条目在真实请求可用，不等于制造工作流通过。新历史恢复没有在此次多文档调用，不据此推断因果。1077最终WPF与探针共享16载荷已核对，实际模型仍排队；新历史恢复测试不能替代模型工作流通过。
- A：真实材质UI、完整性能与图形故障验收继续。C/E组合生产源码现已合入，后续更广输入、关联边界和装配范围仍保持。
- D/F：更广曲面输入、制造规则、交换层级/共享/逐面色/PMI及完整等价继续，不能以当前限定作者契约取代整体规划。

A–F目标继续，G（实机、安装、交付）与Cadoryx清理暂缓。提交这个开发检查点不代表产品验收、安装或发布完成。
