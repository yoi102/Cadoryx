# D2：扫掠／放样实施记录

更新：2026-10-01。**几何、来源依赖、存储和生产窗口已接入；三语言自动化控件与最终联合回归通过，本地限定范围实现完成。** A–F 整体继续进行，第七批仍暂缓。实施契约见 [计划](D2_SWEEP_LOFT_PLAN.md)。

## 当前实现

- [配方](../Cadoryx.Db/GuidedAuthoringRecipes.cs)：有向单开放路径、独立路径／截面帧、明确实体或表面、CorrectedFrenet 与三种拐角过渡。路径覆盖直线、三点弧、二次 Bézier、当前 4–16 控制点均匀三次样条。开放路径使用独立曲线验证，合法共线 Bézier／样条不受“曲线加闭合弦”的面积约束；不能自动重排或反转作者输入。
- 放样保存作者的站位顺序及稳定通道 ID；孔／岛用显式 Parent 关联。各通道截面数必须相同，父链无环且有界；数组排列与孔对应无关。当前契约要求沿首截面法向严格前进、法向不反转，不对用户截面排序。单根轮廓允许方圆过渡，实体／无端盖表面分别构造。任意控制点／非均匀结点仍属于后续 E 项。
- [原生构造](../Cadoryx.Kernel.Occt/OcctGuidedAuthoringGeometry.cs)：单通道不得意外分裂成多个实体，实体失败不得降级为表面。输入、输出检查局部 exact BRep 与独立全局自干涉；交叉的填充截面也检查，不能只检查边界线是否相交。孔／岛用解析面布尔面积、边界距离及全程体积包含核查；最终复合输出重新检查。组合历史未证明，结果不伪造拓扑历史。
- [草图来源](../Cadoryx.Db/GuidedAuthoringSources.cs)：截面及路径各有明确草图 ID／修订／实体选择；路径的 Reverse 是持久的作者选择。放样每通道每站可分别关联来源；同站关联边界必须共面。联合 sketch／datum／feature 图纳入所有来源，C1 草图尺寸及 C2 关联平面通过候选重算驱动；缺失、不相容及失败原子回退，引用草图不能删除。
- [存储](../Cadoryx.IO/GuidedAuthoringSections.cs)：追加 PackGuidedAuthoring Key(16)，复用原有解析／曲线轮廓编码和显式来源 DTO。document **v25**／features **v26**，能力 `cadoryx.guided-authoring.1`。document24→25、features25→26；先保留 D1 的 features24→25 拒绝门禁，再拒绝旧版夹带 D2。拒绝嵌套配方、错误 companion／kind／output 和非法站位数量。

## 当前证据

消费新不可变本地依赖 `8.0.1-preview.28.cadoryx.modeling.sweep.1`，未覆盖旧 mirror.1／mirror.2。锁定包探针 **41/41**，全部 **62 DLL** 与 NuGet 相符：`artifacts/occt-capability-20261001-204643`，日志 `artifacts/d2-native-consumer.log`。实际 bridge SHA256：`A3B60FFA1C7E55F6200F61894B601796AADAF2681D98802D209D27974C1560AD`。

上游新增复制式 Shape.GetSelfInterferenceReport；Release 原生／托管构建、4,117 forwarders、专项 **10/10**、完整 Runtime **609/609**、相对 mirror.2 **1 个新增导出／0 移除**、两个隔离 facade／直接模块消费者及两套 62-DLL 哈希核查通过。证据在 OcctSharp 内层 `artifacts/cadoryx-d2-*`；Debug 原生与上游完整交付门禁 NOT RUN，不与本地依赖验证混淆。

[专项测试](../Cadoryx.Tests/GuidedAuthoringTests.cs) **31/31**：直管／弯管／合法直角管、Bézier／样条路径、实体与表面面积／体积、方圆过渡／多站、孔岛和通道重排、填充截面交叉／断链／分支／反向／大截面干涉拒绝、预览／取消／失败无泄漏、单步撤销重做、来源尺寸／多个草图／基准驱动与原子回退、保存重开／旧版夹带／畸形字段、STEP 原生往返。TRX `artifacts/d2-core-tests/d2-hardened.trx`。完整 Cadoryx 回归905/905、Release零警告/错误；既有桌面及C1–C4/D1/图纸/PDF同版回归PASS（20261001-211128），七份绑定日志空、资产归零。新增最终边面精确绑定/修改后失效专项也通过（含在31项中）；真实桌面指针未验证。

## 生产编辑与联合样本

[编辑器](../Cadoryx.ViewModels/GuidedEditorViewModel.cs)与[窗口](../Cadoryx.wpf/Views/GuidedEditorWindow.xaml)接入 Ribbon 和特征树。显式选择截面和逐条有向路径；站位和通道增删移动、父子对应、每站轮廓赋值；已有完整配方保留、解除来源后保留几何、关联帧禁止直接编辑。所有编辑使候选失效；取消和异步关闭释放候选，旧文档状态禁止确认。曲线列表显示类型与端点，通道显示层级，避免向用户展示内部 GUID。

[编辑器专项](../Cadoryx.Tests/GuidedEditorTests.cs)9/9；新增最终边圆角、上游改变后冻结，以及参数驱动关联工程图和 PDF 样本。D2 总专项 **44/44**（几何35＋编辑器9），TRX `artifacts/d2-complete-tests/d2-complete.trx`。全量首次 **916/916**、Release 零警告/错误；三语言[实际窗口检查](../Cadoryx.wpf/Diagnostics/GuidedEditorSmokeRunner.cs) PASS，证据 `artifacts/d2-guided-smoke-20261001-214242`，6 张截图已检查，绑定日志为空、资产归零。最终完整同版回归 **920/920**、Release零警告/错误、桌面/C1–C4/D1/D2/工程图/PDF PASS（发布20261001-214924），八份绑定日志空、资产归零。日志 `artifacts/d2-final-full-verification.log`；汇总 `artifacts/d2-final-verification.json`。三语言895个非空资源键一致；349 个文档链接与 diff whitespace检查通过。

窗口按钮为明确 WPF Automation Invoke/Toggle；桌面真实指针未验证。下一步 [D3 抽壳/拔模](D3_SHELL_DRAFT_PLAN.md)的实际锁定包探针已通过43/43，应用接入继续；整个 A–F、外部 CAD 与硬件证据继续分别跟踪。
