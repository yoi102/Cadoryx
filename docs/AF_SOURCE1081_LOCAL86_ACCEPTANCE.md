# Source1081 与本地 OcctSharp 86 的应用验收

2026-10-13，status250。A–F 继续，尚未全部完成；G 和清理暂缓。

开发生产源码为提交 `58a9bda` 的1081，仍消费公开 `8.0.1-preview.85`。本轮只在隔离目录 `artifacts/af1081-local86-1/workspace` 消费已独立接收的精确本地 `8.0.1-preview.86`，没有切换生产依赖，也没有公开发布86。

## 已取得的证据

- [候选准备与实际进程审计](../capability-evidence/diagnostics/af1081-local86/verification.json)：1193份代码、测试及工具输入；相对1080仅保留既有主窗口修复并增加15项失败回滚检查。三份原模型提示与断言源码保持。
- 50项目在隔离缓存恢复并锁定恢复成功。50份锁中30份包含SDK，共412项SDK引用；非OCCT包版本、contentHash及依赖保持原样。
- 完整solution的Release、Debug编译均零警告、零错误。两配置实际发现同一6830项测试。
- 应用与测试输出的13个SDK托管DLL、62个原生DLL匹配独立接收的86包。模型探针16个共享应用程序集与候选WPF逐字节一致。
- D6窗口宿主两配置编译通过，19个生产程序集、worker及其配置、SDK13/native62和语言资源匹配候选。原宿主源码、36个精确夹具、六状态及30/300秒条件保持；没有开始桌面输入。
- [A5恢复宿主构建与身份](../capability-evidence/diagnostics/af1081-local86/recovery-build-verification.json)：原24视口恢复程序和36主壳状态栏hook的C#全文保持，两配置四次publish退出0且没有记录警告/错误。当前生产19/worker/语言及SDK13/native62匹配，30/900秒和原48保存文件、52/144整图条件保持；实际运行与整图审阅待完成。
- 恢复程序已保存为[可重建工具](../tools/CadRecoveryWindowProbe/README.md)，不再只依赖旧隔离目录中的C#源码。[工具发布与身份核对](../capability-evidence/diagnostics/af1081-local86/durable-recovery-verification.json)确认两项目/两配置四次publish退出0、原C#全文相同、当前应用及SDK载荷匹配。[审计准备](../capability-evidence/diagnostics/af1081-local86/recovery-auditors-preparation.json)保留原24/36完整运行断言正文，只更新真实候选身份与终态标签；尚未执行恢复运行或审计。
- [Release完整回归终态](../capability-evidence/diagnostics/af1081-local86/release-full-verification.json)已独立核对：实际6830，6806通过、原24严格STEP失败、零跳过；原6652名称及新178完整保留，没有新增失败。实际加载62个原生模块的路径与哈希匹配候选。Debug已串行启动并记录其实际native62，尚未终态；之前的候选准备快照只证明当时运行状态。

## 接续顺序

1. 保留已完成的Release6830，接续当前Debug6830；[完整运行器](../capability-evidence/diagnostics/af1081-local86/run-full.ps1)保存TRX和实际加载模块。[终态审计](../capability-evidence/diagnostics/af1081-local86/audit-full.py)核对原6652名称、新178名称、原失败与新增失败，不放宽严格STEP门禁；不重复已完成的Release。
2. [模型队列](../capability-evidence/diagnostics/af1081-local86/queue-provider.ps1)等待两配置终态审计；只有完整6830、原24失败、零新增失败/跳过及原生身份准确时才进入模型前提。当前已为SDK约30分钟新生成绑定验证设置显式hold：两配置实际结束并核对空闲后才交还SDK，不提前结束当前套件；SDK明确交回且再次观察消费者空闲后才写resume、运行原工程图/工作区场景。保持Bonsai Q1_0、8192上下文、原16工具轮和每轮4分钟；模型未加载或端点变化时停止并保留原因。
3. 原性能程序已重定向到同一候选。保持MiniPC3文件、两fresh进程各20双视口周期、6482ms目标、1800秒期限；生产Open独立两fresh/900秒及完整788×411图像参考保持。性能尚未运行。
4. 当前原生工作结束后做D6、材质和恢复真实窗口；开始键鼠验收前通知用户，只操作任务窗口。
5. 依据实际验收结果决定是否把86依赖、锁及运行时身份接入生产；合适检查点提交推送。开发期本地包不冒充公开包。

OcctSharp现有会话继续CR–DA生成器迁移；Cadoryx发现确需新API时提供具体接口和原断言，优先插入对应批次。当前Cadoryx持有原生验收时段，两侧不并发原生消费者或窗口。性能开始前明确通知SDK暂停占CPU的生成/托管编译，不把重叠运行的样本称为同条件性能证据。

## 未完成与诊断限制

原24严格STEP问题、AppSweep诊断、原模型、性能及窗口尚未收敛；更广草图、参数、曲面终止、复杂拓扑历史装配与制造互操作仍按第4、5节原范围继续。不能将6830发现、编译或运行中模块核对计为A–F完成。

准备阶段两个辅助脚本问题已保存至隔离目录：首协调器在启动恢复后写入裸`false`导致退出，恢复终态未捕获；独立审计首轮误要求testhost直接由外层dotnet启动，实际存在vstest.console中间进程。后续分别完成可观测恢复与完整实际进程链核对，未修改产品、测试断言或候选输入。这些属于验收工具诊断，不算产品修复。

A5首轮构建审计误要求`dotnet publish`提供build总结计数，但该命令仅输出恢复/发布信息；四次退出0的完整日志保留，改核对实际诊断及载荷，没有重新构建或修改原C#。上游报告13:51:29Z已结束的一次隐含bridge增量编译，未运行原生消费者；它与本轮Release全量时间重叠。当时没有性能/窗口/模型运行，不据此虚构性能重跑或测试因果。
