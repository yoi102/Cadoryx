# A5 当前恢复矩阵与主壳状态栏验收

2026-10-13 当前：原24视口恢复程序与36主壳状态栏hook已绑定[1081/本地86候选](AF_SOURCE1081_LOCAL86_ACCEPTANCE.md)，两配置四次publish均退出0，没有记录警告/错误，C#全文保持。[构建与身份审计](../capability-evidence/diagnostics/af1081-local86/recovery-build-verification.json)核对19个当前生产程序集、worker及其配置、语言资源和SDK13/native62；原24/36矩阵、30秒就绪、900秒观察、48保存文件及52/144整图条件保持。[当前运行器](../capability-evidence/diagnostics/af1081-local86/run-recovery.ps1)已准备但未启动，要求当前两配置6830完整终态与精确载荷，运行前通知用户。生产根仍1081/公开85；原STEP24、模型、性能、D6及A–F其余范围继续。以下source1000/SDK26、1063/SDK30记录为历史，不能替代当前验收。

原C#已落入[tools/CadRecoveryWindowProbe](../tools/CadRecoveryWindowProbe/README.md)，使用调用方提供的冻结WPF输出重建。[两配置工具发布](../capability-evidence/diagnostics/af1081-local86/durable-recovery-verification.json)核对四次publish及当前载荷；[独立审计准备](../capability-evidence/diagnostics/af1081-local86/recovery-auditors-preparation.json)保持原运行断言正文，不把准备当作通过。Windows Computer Use已初始化并成功只读枚举窗口，没有启动任务窗口、激活窗口或键鼠输入。

2026-10-12，status209：两份SDK30基线验收程序已编译并核对引用载荷，**均未运行**。生产仍为source1000 / SDK26，A–F未全部完成，G暂缓。1063修订5的Release/Debug完整6467均已独立通过；新1068的65项仍有24项STEP导出失败，不能用基线结论替代最终候选或桌面验收。

## 原24项恢复矩阵

[当前编译证据](../artifacts/af1063r5-wpf-recovery24-draft-1/build-1-verification.json)核对当前WPF/Controls、共享17、SDK13及native62。`Program.cs`与已验SDK29消费者逐字节相同，只更换项目引用路径；原案例、断言和30秒就绪预算保持。

矩阵为两种真实原生状态（presentation数据失效、原生viewer所有者已释放）×完成/渐进场景×单/双pane×恢复/分派前关闭/清理回调失败，共24项。必须检查原文档、历史、文件和资产、精确相机、失败宿主退役、另一pane完整画面、后续原生建模及精确撤销/重做、生产保存重开和零资产。原48份保存文件和52张完整PNG仍是门禁。

新运行器用原始流保存stdout/stderr，严格要求stderr为0字节；不再经过原先给空流追加CRLF的`Set-Content`。旧2字节记录及其正反对照证明保留。新审计保留原24项状态、生命周期、历史、图像和保存检查正文；图像仍需逐张完整字节核对或直接审阅。

## 新36项主壳状态栏矩阵

[主壳夹具编译证据](../artifacts/af1063r5-main-status-recovery-draft-1/build-2-verification.json)只证明语义编译及当前WPF/SDK30引用一致，运行未开始。首轮CS8602空引用编译失败和原源码保留，修订使用明确非空的实际replacement viewport。

| 维度 | 原条件 |
| --- | --- |
| 状态消息语言 | en-US、zh-CN、ja-JP；仅验证恢复消息，不据此声称整个界面完成三语言验收 |
| 原生失效 | 实际`InvalidateGpuResources`状态、实际所有者释放；通过原宿主绘制消息进入既有Error/恢复路径 |
| 路径 | 恢复成功、实际文档在分派前detach、实际Destroying回调失败 |
| 失败视口 | 主pane或第二pane，均作为当前活动pane；另一pane保持原所有者和完整画面 |
| 总数与期限 | 36项；每项30秒就绪期限，整次900秒观察期限，不抢占原生调用 |

夹具使用真实生产MainWindow及绑定`StatusText`的实际TextBlock，不调用私有恢复方法，不写入状态文本，不注入OS键鼠。启动前必须核对私有诊断恢复目录和内存资产存储。每例保存故障前后文档，核对文档/代次/历史/资产及当前存储状态不变；完成原生编辑、精确撤销/重做、保存重开和最终清零。

主壳最终状态必须与实际TextBlock一致，正常恢复清除提示，清理失败显示本地化失败消息，分派前detach不发布迟发状态。恢复开始的属性变化和绑定文本记录独立保存；不将短暂消息记录当作物理屏幕已经扫描显示的证据。144张整图包括独立原生视口整图与完整WPF主壳渲染，后者不替代桌面像素或物理输入验收。

## 运行门禁与剩余证据

[运行器和独立审计工具](../artifacts/af-sdk30-history-identity-draft-5/wpf-recovery-acceptance-tools-1.json)已核对静态来源、语法、原24断言正文及日志方式。两份运行器要求当前完整6467两配置及匹配应用载荷；所有原生/WPF运行串行，准备不会自动打开窗口。涉及桌面的实际运行前提示用户，现有授权不重复询问。

新36项补充原24项，不能替代它。实际缺失/无效图形上下文、物理GPU故障仍未取得证据；原生失效标记、所有者释放或编译通过不能证明这些故障。所有通过结论还需实际运行结果、native62路径/哈希、完整矩阵、模型/历史/资产和图像审阅分别确认。当前生产合入尚未完成。

当前1063编译成果是基线准备。[旧最终1066应用队列](../artifacts/b18-single-loop-mixed-joints-draft-2/final-app-after-baseline-1-exit.json)已因相关980失败而停止，未发布1066。最新1068的求解改进和剩余STEP失败见[当前状态](STATUS.md)；修正导出并通过最终对应的完整矩阵后，原24和新36须保留原源码/预算、绑定该最终WPF/SDK载荷重新编译并实际运行。基线1063编译或较早恢复结果不能代替最终候选验收。
