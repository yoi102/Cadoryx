# 材质、F5 与视口恢复的统一接续

2026-10-11。A–F 按用户指令继续；桌面验收已重新授权，每次开始前提示。G 实机矩阵、安装与交付暂缓。

## 已完成的验证

F5/Renderer 修复候选完整 Release、Debug 各 5,998 项通过，零失败、零跳过。相同候选的圆盘和综合窗口通过原七次静止换页、400 毫秒、180/240 秒及 12 次停靠关闭条件；两组共 60 张完整图像审阅通过。鼠标移动保持已显示页面，键鼠占用已释放。见[完整两配置](../artifacts/a5-cleanup-f5-integrated-draft-1/full5998-both-2-verification.json)、[圆盘](../artifacts/a5-cleanup-f5-integrated-radial-window-1-verification.json)、[综合窗口](../artifacts/a5-cleanup-f5-integrated-general-window-1-verification.json)和[完整图像](../artifacts/a5-cleanup-f5-integrated-window-images-1-verification.json)。物理滚轮延迟仍未测量。

普通实体和来源样式的控制、渐进失效、完成后失效共六项真实会话恢复通过：快照、代次、历史、保存状态与路径保持；新视口从当前快照及原相机重建完整相同画面；失效后实际建模、精确撤销重做和生产保存重开通过；最终资产归零。24 张完整图像审阅通过。见[恢复运行](../artifacts/a5-session-viewport-recovery-draft-1/runtime-3-verification.json)和[图像](../artifacts/a5-session-viewport-recovery-draft-1/images-3-verification.json)。修正后的夹具明确新来源的当前 XDE 格式，首轮编译失败与第二轮三项样式重开失败保留。此证据来自自有隐藏视口，尚不证明 WPF 自动重建或物理 GPU 丢失恢复。

## 实际 WPF 泄漏与修复

原四项实际 HwndHost 探针复现：私有原生 viewer 已关闭时，销毁中的 CancelInput 抛 ObjectDisposedException，窗口、视口、注册表和一个资产残留；若 Destroying 回调先失败，原异常也被覆盖。见[原观察](../artifacts/a5-wpf-host-cleanup-probe-draft-1/runtime-1/result.json)。

统一候选现在逐步尝试所有销毁动作，先清除视口引用再释放捕获，最终销毁不再发送无必要的原生 PointerReleased，保留首异常并记录后续清理异常。面板拆卸先清除宿主引用，在 finally 中销毁，并确保渐进状态清理。相同已编译四项探针仅替换 WPF DLL 后全部通过；结果在任何夹具兜底前记录。实际 ViewportPane Loaded/Ready 与真实文档 VM 又验证了正常/完成失效/渐进失效、回调成功/失败、文档 detach/DataContext 切换共 12 项：面板引用、HWND 与原生资源归零，文档和历史保持，之后可真实建模及精确撤销重做。见[四项修复](../artifacts/a5-wpf-host-cleanup-probe-draft-1/runtime-2-verification.json)和[十二项面板运行](../artifacts/a5-wpf-pane-cleanup-consumer-draft-1/runtime-2-verification.json)。该隐藏面板消费程序未验证主壳状态栏或物理 GPU 丢失。

## 当前统一候选

生产仍是缓存修复基线 1000 / SDK26。材质与共享命令候选独立 Release、Debug 各 6,020 项通过。合并候选位于 [workspace-3](../artifacts/a5-material-f5-cleanup-unified-draft-1/workspace-3)，保留原 5,909、F5 新增 89 和材质新增 111 项，共 6,109 项。两配置全方案构建零警告/错误，各 200 专项和 789 相关通过，原断言、预算、SDK 和实际 62 个原生库保持。见[统一两配置验证](../artifacts/a5-material-f5-cleanup-unified-draft-1/build-related-both-3-verification.json)和[完整6109冻结](../artifacts/a5-material-f5-cleanup-unified-draft-1/full6109-preparation-1.json)。完整 Release、Debug 各 6,109/6,109 已独立通过，零失败/跳过，耗时 46.0876/40.4771 分钟；原逐名清单、冻结源码/载荷和实际 62 库保持。见[两配置完整6109](../artifacts/a5-material-f5-cleanup-unified-draft-1/full6109-both-1-verification.json)。同一候选已发布并通过圆盘第二轮与综合窗口：14 次静止换页、12 次停靠关闭和 60 张完整图像通过；第一圆盘轮的确认标记超时保留，键鼠占用已释放。见[当前完整画面与窗口](../artifacts/a5-unified-window-images-2-verification.json)。

首次合并在同一 Renderer 文件出现四处冲突，失败与原冲突文件保留。后续逐处组合材质配置/纹理清理与 F5/所有者错误保留行为；清理失败不能阻断其余所有者释放或覆盖原操作错误。新 WPF 修复在这一统一候选上，不能把旧 F5 窗口通过直接计为新候选窗口通过。

## 接下来完成的批次

1. 统一候选完整两配置6109、当前圆盘/窗口及原编译材质82/346图已经通过。继续材质生产界面的纹理文件选择/取消、作者/分配/撤销/保存重开与三语言/深浅主题；组件图像不代替界面验收。
2. SDK27显式首显示模式已完成构建、实际Debug、完整两配置1346、14私有包和公开旧42/28/32+新10/40整图验收。初次wrong-thread分析器失败、缺证据目录18失败及审计器三项错误保留；对应修复不改原消费者断言。首模式小改动与控制标记回复已准备在[新统一来源](../artifacts/a5-sdk27-control-unified-draft-1/preparation-1.json)，保留原6109加新13，期望6122。先完成锁恢复/两配置构建/相关/全量及实际SDK27材质/选择/恢复/窗口，再测原MiniPC3两fresh进程、各20双视口周期和6482ms目标。
3. 用原模型、提示、上下文与工具/轮次预算继续真实供应商工程图和多文档工作流。保持原 6,800.2534 毫秒性能失败及 bonsai 两项失败记录。
4. 完成相应生产接入，并继续 B18 通用编辑、完整原生拓扑历史、装配与制造/交换剩余矩阵。正式验收进度仍 A4 / B17 / C5 / D5 / E4 / F5g；不能将构建或部分回归当作 A–F 完成。

真实模型控制标记回复修复的原 21+新 13 实际消费程序 Release、Debug 各 34 项已独立通过；仍保持一次恢复、原轮次、原始历史和严格身份。已合入新隔离来源，尚未取得统一应用构建/回归终态或复跑原供应商工作流，见[专项接续](F5_CONTROL_ONLY_RESPONSE_PLAN.md)。

SDK27 首次 Release 原生和13托管模块通过；完整测试项目因阻塞 Task 写法被 xUnit1031 拒绝。新副本只将该检查改为显式专用线程并 Join，保持创建/使用/释放 viewer 的原线程及全部断言，不禁用分析器。Release 原生源码完全相同，使用冻结的已有产物；实际 Debug 构建继续。后续30/1346两配置、本地14包和公开旧42/28/32+新10完整画面消费均另验；原失败和包准备期间的已有LICENSE断言失败保留。

2026-10-11 status177：SDK27完整两配置和公开消费已通过；详见[公开审计](../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-initial-display-27-draft-1/public-consumer-6-verification.json)与[40图](../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-initial-display-27-draft-1/public-images-1-verification.json)。统一SDK26材质82/346图全部通过，见[图像与运行](../artifacts/a5-unified-material82-consumer-1/images-1-verification.json)。这些证据不证明新SDK27应用的原6482ms性能或真实物理GPU恢复。

2026-10-11 status178：新SDK27统一候选两配置构建/实际6122发现及各802相关已通过。原6122完整两配置和当前SDK27应用门禁仍须取得独立终态；其后继续[实时表达式生成点链270待验场景](B18_LIVE_EXPRESSION_POINT_COUPLING_PLAN.md)。本批八文件语法已验，不把源码准备计为B18完成。

2026-10-11 status179：统一候选完整Release6122已独立通过，Debug继续。当前[自动恢复源码核对](../artifacts/a5-sdk27-control-unified-draft-1/automatic-recovery-source-audit-1.json)确认仍为报告异常；需要先证明渲染/设备失效分类，再有界合并恢复请求、退役旧捕获/弹窗/动画/渐进回调、按当前快照与相机重建。普通非法几何应保留原子拒绝，不能循环重启。真正WPF双视口/关闭竞争/重建失败/完整画面/后续建模保存及零资产仍待验。

2026-10-11 status180：新SDK27统一候选完整两配置各6122、当前WPF17/SDK13/实际62、原82材质/346整图，以及当前圆盘14次静止前台像素/12窗口关闭循环/60整图已独立通过。当前[窗口完整图审阅](../artifacts/a5-sdk27-control-window-images-1-verification.json)不替代材质实际picker、自动恢复或硬件滚轮延迟；原性能目标和供应商实跑继续。

2026-10-11 status181：当前[性能独立终态](../artifacts/a5-sdk27-control-performance-verification-3.json)满足原两fresh/各20周期/6482ms和生产Open整PNG要求，首次图像失败保留。当前[实际材质作者/文件核验](../artifacts/a5-sdk27-material-desktop-3-verification.json)通过，保留观察器失败、offscreen Apply NO-OP及忙碌状态比较错误；密度显示尾数待独立候选复验。自动恢复仍为report-only，未将材质、手动重建或性能通过冒充自动/物理GPU恢复。

2026-10-11 status182：[密度G15实际窗口](../artifacts/a5-density-display-desktop-1-verification.json)证明显示7850而原始浮点及物理密度、文档和文件保持；独立候选未应用生产。首构建缺引用程序集、核验脚本基线路径替换错误及测试空白文件前斜线路径拒绝保留。B18两配置各270专项已验，原6122及容差/预算不改；完整6392和C1仍待验。自动恢复需要先桥接OCCT真实IsDeviceLost信号，不能把泛化OcctFailure/非法几何当设备失效。

2026-10-11 status183：见[当前合并验收](AF_MERGED_LIVEPOINT_DRAWING_DENSITY_PLAN.md)。两配置6422发现及各350联合通过，完整6422运行中；C1实际窗口未运行，Bonsai工程图/多文档最新两项FAIL已独立保留。SDK28原生健康状态8文件/12测试为未编译草稿，WPF自动恢复尚未完成。生产1000/SDK26和正式子批进度保持，G暂缓。

## status191 OcctSharp 生成器承接

用户明确提醒新封装必须考虑代码生成器。SDK28的手工桥接、全部测试和公共消费者作为冻结候选保留；它们不能证明生成器可以重建封装。SDK28加恢复的旧应用构建入口没有启动。新[SDK29准备](../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-1/preparation-1.json)复制原1031来源后移除三份手工底层文件，增加生成器、模板/契约及11条规则测试，共1034来源。原175生成文件及原全部测试逐字节保持；三份新生成输出还不存在，不能记为生成已通过。

`ViewerSupportWorkflowEmitter`由标准generate入口调用，配置显式选择`render-health-v1`，四份OCCT 8.0.1头文件逐字节哈希校验。未知/重复规则、版本或头文件变化会在写出前拒绝；不公开借用的图形上下文、驱动或结构管理器。模板生成C ABI头/实现和当前NativeMethods调用，保留原interop程序集和高层门面的线程/ABI/未知标志检查。支持胶水生成不增加已迁移OCCT声明StableId；现有Manual/Emitted覆盖统计不能因此提高。

[四文件语法](../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-1/syntax-1-verification.json)通过仅证明解析。已启动的[串行入口](../artifacts/run-sdk29-viewer-generation-after-full-1.ps1)等待当前F5完整两配置6448独立通过，之后才编译生成器、运行两配置规则/原套件及实际两次生成。原175文件、StableId、完整manifest/coverage/diagnostics/closure和模板溯源必须保持或具有准确的3文件增量。实际原生/托管两配置构建、导出/API无增减、1358语义/生命周期、公用消费者及WPF上下文故障矩阵继续，不将生成成功当作编译或运行通过。

A–F继续；生产1000/SDK26及正式A4/B17/C5/D5/E4/F5g保持，G用户暂缓。桌面输入未开始，开始前提示保持。
