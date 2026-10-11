# A–F 暂停检查点（2026-10-11）

status196。用户要求在合适位置停止、做一次本地提交，并清理无用记录和缓存。本次在导航专项终态后暂停；**A–F 未全部完成，G 实机、安装及交付继续暂缓**。正式已验子批次保持 **A4 / B17 / C5 / D5 / E4 / F5g**。

## 当前提交的应用代码

当前应用仍为 **source1000 视口缓存/租约修复版 / SDK26**，版本为 `8.0.1-preview.31.cadoryx.curve-inspection.26`。本次收尾逐文件核对[原构建清单](../artifacts/a5-reference-cache-repair-build-1-verification.json)中1000份来源的SHA-256，全部一致。新导航、SDK29和恢复草稿保留在各自隔离目录，未自动合入。

该基线的[Release完整5909](../artifacts/a5-reference-cache-repair-release-full5909-1-verification.json)、[Debug完整5909](../artifacts/a5-reference-cache-repair-debug-full5909-1-verification.json)、[圆盘静止像素换页](../artifacts/a5-reference-cache-repair-radial-window-2-verification.json)、[综合窗口](../artifacts/a5-reference-cache-repair-general-window-1-verification.json)及[60整图](../artifacts/a5-reference-cache-repair-window-images-1-verification.json)已验。生产direct第二轮6800.2534ms超过原6482ms，[性能失败](../artifacts/a5-reference-cache-repair-performance-verification-2.json)保留，不能借隔离候选成绩称生产达标。

当前锁包依赖仍来自[NuGet.Config](../NuGet.Config)中的本地私有SDK26源。包、实际原生载荷与源码快照保留；本地提交不等同于可公开还原或产品交付。

## 隔离候选的终态

| 候选 | 已通过 | 未通过或未运行 |
| --- | --- | --- |
| source1056 / SDK27 | 两配置各6422；C1 18 WPF流程/18保存文件/54整图；材质82场景/346整图；14前台静止采样、12窗口循环/60整图；MiniPC3两fresh进程各20双视口周期，完整提交4.752/5.061秒，两次Open保持原788×411完整图像 | 当前picker/G15、真实点输入及生产合入 |
| source1057 / SDK27，检查角色反馈 | Release、Debug各6448/6448，零失败/跳过；原Bonsai跨文档8轮/29调用，关闭资产0 | 工程图完成2轮/15调用，第三轮空答且缺dimension create成功收据，FAIL |
| source1058 / SDK27，上下文导航 | 两配置构建各0警告/0错误；发现原6448+新3=6451；相关各528/528，原测试/预算保持 | 完整6451、原模型工程图和跨文档复验、生产合入：NOT RUN |
| SDK29生成器封装 | 两配置生成器各218=原207+11；两次标准入口生成一致；原175输出逐字节保持，新增3支持胶水；四个OCCT8.0.1头文件哈希钉定，无StableIds或声明覆盖信用增加 | 原生CMake编译器探测长路径失败，尚未编译产品C++；托管SDK、各1358、14包和公开消费者：NOT RUN |
| source1059 / SDK29，WPF恢复 | 恢复源码及语法检查；24场景真实WPF消费者源码和runner已准备 | SDK29前置失败后未restore/build/run；实际上下文缺失/无效、物理GPU移除和主壳状态栏仍NOT RUN |

对应证据：[1056窗口与完整图](../artifacts/af-merged-window-images-1-verification.json)、[1056性能](../artifacts/af-merged-performance-verification-2.json)、[1057两配置完整6448](../artifacts/f5-inspection-role-feedback-draft-1/full6448-both-1-verification.json)、[原模型跨文档PASS](../artifacts/f5-inspection-role-feedback-draft-1/lmstudio-workspace-1-verification.json)、[工程图FAIL](../artifacts/f5-inspection-role-feedback-draft-1/lmstudio-drawing-1-verification.json)、[1058构建/专项独立核验](../artifacts/f5-native-read-navigation-draft-2/build-focused-both-1-verification.json)、[SDK29生成审计](../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-2/generation-build-1-verification.json)、[SDK29原生失败](../../OcctSharp/OcctSharp/artifacts/cadoryx-a5-render-health-generated-29-draft-2/native-managed-build-1-exit.json)及[WPF24前置拒绝](../artifacts/a5-sdk29-wpf-recovery-consumer-draft-1/build-runtime-1-exit.json)。

工程图失败的实际native查询有45项，CylinderFace在index38；HTTP保留前20项，原结果已经含`contextPage.nextRequest`与`omittedGeometryReads`的offset38提示，模型未读取。不能归因为原生未返回或缺续页元数据。1058只把导航放在长items前，并区分上下文`hasMore`与原查询`queryHasMore`；不自动查询重放或猜测身份，原6000/2400字符、16工具轮、8192上下文保持。

首导航草稿的2项失败、SDK29首CA1861、版本字段错误、队列拒绝和长路径失败均保留原记录。修订没有放宽断言或预算。本次没有新启完整6451、WPF24或模型推理。

## 文档与缓存整理

当前状态、规划和跟踪页保持暂停终态。历史过程快照已由[Git历史索引](history/README.md)提供固定版本链接，工作目录不再重复保留33份全文；原失败和真实验收材料继续保留。清理限定于本仓库，没有修改OcctSharp工作区或全局NuGet缓存。

前两轮清理累计删除 **532个缓存目录、28,682个文件，约3.18 GiB**，当时保留了三个被证据引用的`bin`目录和103个中间目录；详见[首轮清单](../artifacts/checkpoint-cleanup-executed-1.json)和[推送后清单](../artifacts/post-push-cleanup-executed-2.json)。应用提交`6926892`已推送到`origin/master`。

记录整理另清理 **467项，约10.33 MiB**：194份已执行的一次性文档脚本、11份过期文档状态汇总、166份无引用空日志、63份有保留原本的完全重复输出，以及33份已在Git保存的文档快照。实际验收JSON/TRX、失败记录、当前来源的诊断、待续源码、私有SDK包和实际payload保留。[删除及恢复清单](../artifacts/records-cleanup-executed-1.json)记录每项哈希、理由和可用的恢复位置。

随后按用户要求清理整个仓库的磁盘占用：清理前 **193.46 GiB**，删除 **207,747份旧编译文件、178.96 GiB**，复核时剩余 **14.61 GiB**，无删除失败。主要删除历史发布输出、旧`bin`/`obj`、测试程序副本和原生探针编译产物。当前生产`bin`、source1000运行候选、source1056最新工作区及运行候选、source1057/1058/1059待续目录、全部源码、私有包、模型、截图、JSON/TRX、日志与故障转储继续保留。旧验收清单中的历史二进制路径可能已不存在，重放旧版本前须重新编译；历史通过/失败结果不因此改写。

[执行清单](../artifacts/storage-cleanup-20261011-executed.json)与[独立复核](../artifacts/storage-cleanup-20261011-verification.json)记录了范围和结果：180,110份保留文件均存在且大小/修改时间未变，生产1000份源码SHA-256一致，147份私有/工具包归档仍在。未修改OcctSharp或全局NuGet，也未重跑产品测试。磁盘清理的两份大清单已有压缩副本；自动审批拦截了删除未压缩原本的补充操作，约103 MiB原本仍保留。

## 恢复时的顺序

1. SDK29改用短构建目录，重做原生/托管语义构建、1358两配置、包/实际哈希和公开消费者；从生成器配置、模板或类型映射修改并再生成，不手改生成输出。
2. 继续1058完整6451与原模型复验，核对工程图实际create收据；保留原模型/提示/预算和严格身份守卫。
3. 完成1059语义构建和24场景WPF恢复：两种损失×完成/渐进×单/双pane×恢复/关闭前取消/清理回调失败；核对后续建模、精确undo/redo、保存重开和资产归零。实际上下文/物理GPU故障分别验收。
4. 完成当前picker/G15、真实点编辑及其余A–F缺口，再考虑通过候选的生产合入；桌面键鼠验收开始前按用户要求提示。

更广的未完成能力继续按[产品规划](CAD_PRODUCT_GAPS.md)与[A–F跟踪](AF_IMPLEMENTATION_TRACKER.md)推进；不把本次提交称为全部完成或交付。G保持用户暂缓状态。
