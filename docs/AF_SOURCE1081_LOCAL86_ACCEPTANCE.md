# Source1081 与本地 OcctSharp 86 的应用验收

2026-10-14，接续status252。A–F继续，尚未全部完成；原24项严格STEP失败按用户授权延期，G和清理暂缓。动态进程状态以实际观察和终态收据为准。

status253终态更新：[两配置6830](../capability-evidence/diagnostics/af1081-local86/full-verification.json)现已全部实际运行并审计，各6806/原24/0跳过，无新增失败、原用例和native62保持。[Far90修订6](../capability-evidence/diagnostics/af1081-local86/far90-runtime-6-verification.json)完整执行各72/18失败，未成为产品验收；原90矩阵/公差未改变，深复制隔离后所有原source/stop BRep字节准确。新的25052协调已在模型预启动hold，根实际核对空闲后明确交还SDK CX/CY资格时段。下方原准备/运行中描述保留阶段事实，最新以实际终态及STATUS为准。

开发生产源码为提交 `58a9bda` 的1081，仍消费公开 `8.0.1-preview.85`。本轮只在隔离目录 `artifacts/af1081-local86-1/workspace` 消费已独立接收的精确本地 `8.0.1-preview.86`，没有切换生产依赖，也没有公开发布86。

## 已取得的证据

- [候选准备与实际进程审计](../capability-evidence/diagnostics/af1081-local86/verification.json)：1193份代码、测试及工具输入；相对1080仅保留既有主窗口修复并增加15项失败回滚检查。三份原模型提示与断言源码保持。
- 50项目在隔离缓存恢复并锁定恢复成功。50份锁中30份包含SDK，共412项SDK引用；非OCCT包版本、contentHash及依赖保持原样。
- 完整solution的Release、Debug编译均零警告、零错误。两配置实际发现同一6830项测试。
- 应用与测试输出的13个SDK托管DLL、62个原生DLL匹配独立接收的86包。模型探针16个共享应用程序集与候选WPF逐字节一致。
- D6窗口宿主两配置编译通过，19个生产程序集、worker及其配置、SDK13/native62和语言资源匹配候选。原宿主源码、36个精确夹具、六状态及30/300秒条件保持；没有开始桌面输入。
- [A5恢复宿主构建与身份](../capability-evidence/diagnostics/af1081-local86/recovery-build-verification.json)：原24视口恢复程序和36主壳状态栏hook的C#全文保持，两配置四次publish退出0且没有记录警告/错误。当前生产19/worker/语言及SDK13/native62匹配，30/900秒和原48保存文件、52/144整图条件保持；实际运行与整图审阅待完成。
- 恢复程序已保存为[可重建工具](../tools/CadRecoveryWindowProbe/README.md)，不再只依赖旧隔离目录中的C#源码。[工具发布与身份核对](../capability-evidence/diagnostics/af1081-local86/durable-recovery-verification.json)确认两项目/两配置四次publish退出0、原C#全文相同、当前应用及SDK载荷匹配。[审计准备](../capability-evidence/diagnostics/af1081-local86/recovery-auditors-preparation.json)保留原24/36完整运行断言正文，只更新真实候选身份与终态标签；尚未执行恢复运行或审计。
- [Release完整回归终态](../capability-evidence/diagnostics/af1081-local86/release-full-verification.json)已独立核对：实际6830，6806通过、原24严格STEP失败、零跳过；原6652名称及新178完整保留，没有新增失败。实际加载62个原生模块的路径与哈希匹配候选。原Debug作业已实际中断并缺TRX/exit，[中断核验](../capability-evidence/diagnostics/af1081-local86/debug-interruption-verification.json)保留旧日志和冻结载荷；只重跑缺终态Debug，输出`full6830-debug-2`。实际协调50296/dotnet47124/testhost15932及native62已核对，尚未完整终态。
- [用户延期名单](../capability-evidence/diagnostics/af1081-local86/step24-user-deferral.json)精确列出24个参数化用例，与历史两配置失败逐项一致。它们保留失败结果和严格断言，不豁免其他交换用例；不等待其修复即可推进其他A–F。
- [Far90前提两配置编译/载荷冻结](../capability-evidence/diagnostics/af1081-local86/far90-build-verification.json)已通过，原30项名称、完整孔和递归岛、生产体积/面积预算保持。72正例及18反例仍未运行，不代表产品Far算法已实现。

## 接续顺序

1. 保留已完成的Release6830，[缺终态Debug运行器](../capability-evidence/diagnostics/af1081-local86/resume-debug.ps1)只接续原冻结Debug，保留原中断目录；[终态审计](../capability-evidence/diagnostics/af1081-local86/audit-full.py)核对原6652名称、新178、原失败及新增失败，未修改严格断言，不重复Release。
2. SDK已经明确交回生成绑定原生验证时段；`debug-native-resume.json`记录实际交回和空闲观察。新的[串行跟进程序](../tools/FiniteSurfaceFarProbe/run-after-debug.ps1)实际协调14204按PID和创建时间观察50296，完整两配置审计后先运行两配置Far90并独立审计，再经过新的实际空闲检查续接原[工程图/工作区模型程序](../capability-evidence/diagnostics/af1081-local86/run-provider.ps1)。旧模型队列的协调进程已经消失，不能只凭旧hold或state文件称它仍在运行。保持Bonsai Q1_0、8192上下文、原16工具轮和每轮4分钟；模型未加载、端点变化、新回归或终态缺失时停止并保留证据。
3. 原性能程序已重定向到同一候选。保持MiniPC3文件、两fresh进程各20双视口周期、6482ms目标、1800秒期限；生产Open独立两fresh/900秒及完整788×411图像参考保持。性能尚未运行。
4. 当前原生工作结束后做D6、材质和恢复真实窗口；开始键鼠验收前通知用户，只操作任务窗口。
5. 依据实际验收结果决定是否把86依赖、锁及运行时身份接入生产；合适检查点提交推送。开发期本地包不冒充公开包。

OcctSharp现有会话继续CR–DA生成器迁移；Cadoryx发现确需新API时提供具体接口和原断言，优先插入对应批次。当前Cadoryx持有原生验收时段，两侧不并发原生消费者或窗口。性能开始前明确通知SDK暂停占CPU的生成/托管编译，不把重叠运行的样本称为同条件性能证据。

2026-10-14调度更新：SDK另外请求CX25材质/CY8色标的约30分钟原生资格时段。模型运行器已加入显式交接等待；请求本身不算交还。只有Far90两配置实际终态、模型尚未启动、协调进程身份准确和原生消费者实际空闲时，[交还检查器](../capability-evidence/diagnostics/af1081-local86/grant-sdk-transition.ps1)才记录可交还边界，再由根向现有SDK会话明确发送。SDK明确交回后记录返回收据并重新检查冻结来源、模型载荷及真实空闲，继续原模型。当前SDK86及原提示/断言/预算没有改变。

## 未完成与诊断限制

原24严格STEP问题延期修复；AppSweep诊断、完整Far产品集成、原模型、性能及窗口仍未收敛。更广草图、参数、曲面终止、复杂拓扑历史装配与制造互操作按第4、5节原范围继续。不能将6830发现、编译、延期失败或运行中模块核对计为A–F完成。

准备阶段两个辅助脚本问题已保存至隔离目录：首协调器在启动恢复后写入裸`false`导致退出，恢复终态未捕获；独立审计首轮误要求testhost直接由外层dotnet启动，实际存在vstest.console中间进程。后续分别完成可观测恢复与完整实际进程链核对，未修改产品、测试断言或候选输入。这些属于验收工具诊断，不算产品修复。

A5首轮构建审计误要求`dotnet publish`提供build总结计数，但该命令仅输出恢复/发布信息；四次退出0的完整日志保留，改核对实际诊断及载荷，没有重新构建或修改原C#。上游报告13:51:29Z已结束的一次隐含bridge增量编译，未运行原生消费者；它与本轮Release全量时间重叠。当时没有性能/窗口/模型运行，不据此虚构性能重跑或测试因果。
