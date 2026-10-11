# E3：复杂边链与半径规律实施记录

更新：2026-10-04。**E3当前本地契约实现及验证完成：61专项、最终2126/2126全量、Release零警告/错误，同冻结七窗口PASS，程序集及27份截图审计通过。** 契约见 [计划](E3_CONTOUR_LOCAL_EDITING_PLAN.md)，总证据 `artifacts/e3-contour-verification-3.json`。A–F仍进行中，G第七批暂缓。

## 已接入的核心

[半径法则](../Cadoryx.Db/ContourRadiusLaws.cs)包含常量、线性、平滑、完整采样和正控制点样条；稳定样本/控制点ID为参数驱动保留身份。平滑法则以规范化Hermite的四个Bézier控制值证明正性，样条限制clamped的[0,1]域；采样最终由native证明插值控制包络为正。

[配方](../Cadoryx.Db/ContourLocalRecipe.cs)保存稳定轮廓程序ID、完整源/种子/支撑面，以及原生实际有序链、端点索引、闭性和完整长度/参数区间。顶点索引属于种子所绑定的同一原生完整map；没有修改旧ExactTopologySelection的Face/Edge契约。全部程序共享精确来源，不允许链重叠或重复分配。

[发现和几何](../Cadoryx.Kernel.Occt/OcctContourLocalGeometry.cs)提供只读DiscoverContourAsync；每次构造都重新核对完整原生链、顺序、方向、参数和来源指纹。一次native执行多轮廓圆角/倒角，exact有效、单实体和自干涉检查不变。FinishLocal保留并核验真实native历史；不能证明的映射继续诊断，不补造身份。

[命令](../Cadoryx.Commands/ContourLocalCommands.cs)消费当前完整显示实体，保留实体色/材料/图层/可见性，逐面样式没有证明就清除。新建、取消/失败和重选通过已有候选事务；普通重算不能偷换来源，上游变化冻结旧轮廓，完整明确重选后恢复。历史许可名单及实际native重放已加入新配方，仍有完整来源/结果资产和revision检查。

## 参数、存储与生产作者

[稳定参数字段](../Cadoryx.Db/ContourParameterTargets.cs)以程序、样本和控制点ID承载半径、Hermite导数及倒角距离；程序/控制点重排不改变驱动身份。更换规律家族或删除受驱动行必须先解除绑定，种子、方向和链证据不由表达式驱动。

[存储companion](../Cadoryx.IO/ContourLocalSections.cs)完整保存五种规律、行ID、种子/支撑及实际原生链；document41/features37与capability `cadoryx.contour-local.1` 独立演进。D4 document40/features36经过明确中间版本迁移；旧格式新字段夹带、kind/companion混用及规律污染拒绝。保存和重开都从配方保留的历史Source重新执行native复证，没有resolver时明确拒绝；冻结/抑制配方不借当前来源冒充旧证据。

[生产编辑器](../Cadoryx.ViewModels/ContourLocalEditorViewModel.cs)支持完整程序/规律/样本/控制点/结点编辑、增删重排、原生Edge/Face选择、完整链高亮和原子预览。来源改变后每个旧程序必须明确重选；取消、关闭、状态变化丢弃晚到的发现或几何。Ribbon、特征树与三语言[窗口](../Cadoryx.wpf/Views/ContourLocalEditorWindow.xaml)使用同一契约。单实体生成、导入、抽壳和偏移最终结果均有专项样本。

[结构化作者](../Cadoryx.ViewModels/Toolboxes/CadContourAuthoring.cs)新增 `contour_local`（作者类型14，工具仍12），创建/完整编辑/明确重选消费exact种子并重新发现链，拒绝作者提供native链缓存。`cad_model action=inspect_contours`返回分页完整作者和只读原生链；旧state、链夹带和失效来源拒绝。精确种子通过 `cad_datum action=inspect_sources` 获取。

## 当前证据与最终候选

SDK前提 `artifacts/e3-contour-sdk-2`：10/10、62包内/实际加载DLL匹配，完整链线性法则与独立截面公式核对，正例STEP重读；它不能替代应用验收。

领域、内核、命令核心构建各0警告/错误。首次应用 `e3-contour-core-1` 为4通过/8失败，原因是新配方未加入已有历史允许名单；修复保留来源/结果检查后，`artifacts/e3-contour-core-2/results.trx` **12/12**通过：五法则作用整个两段原生链、独立两圆角/两双距离倒角解析体积、链顺序/长度/端点/成员夹带拒绝及原子资产/历史、上游修改后冻结和显式发现/重选恢复。

`artifacts/e3-contour-hardening-2/results.trx` **61/61**专项通过，包含完整链篡改拒绝、五规律参数/native重开、两距倒角、閉链、冻结/重选、最终边后继、工程图刷新/PDF/STEP、offset和hollow-shell来源，以及六种晚到发现/预览组合。早期联合 `e3-contour-editor-author-1` **148/148**、`e3-contour-integration-2` **102/102**分别保留；首次integration的5项失败是ImmutableArray容器身份比较，改为逐成员比较后通过，native完整链要求未放宽。`artifacts/e3-contour-build-1.log`为最新全方案Release，0警告/错误。

第一版冻结 `artifacts/publish/e3-contour-20261004-1` 三语言窗口PASS，绑定日志为空、关闭后资产归零。其24张截图中检查四张英文布局后，修复清除按钮文案、链表头换行与表格高度，并增加样条下半页截图。第二版七个自动窗口也全部PASS，但人工像素检查发现独立链表头未继承主题，浅色背景配浅色文字；保留第二版并修复BasedOn，不把自动化通过当布局可用。

最终冻结 `artifacts/publish/e3-contour-20261004-3` 的E3/D4/D3/C1/D2/desktop/drawing **七窗口PASS**，七份绑定日志空、关闭资产归零；`e3-contour-windows-3-summary.json`。最新27份PNG中的15张中/英/日布局和4张独立native预览直接逐张检查，余下8张native与已看图逐字节相同，见 `e3-contour-images-3.json`。WPF布局截图不含独立HWND原生像素，两类图分别检查；样条下半页显示完整控制点/结点及按钮。

`e3-contour-full-1/results.trx` **2126/2126**，0失败/跳过，11m16s。全方案Release0/0后，最终仅WPF表头样式修改，`e3-contour-wpf-build-3.log`同为0/0。`e3-contour-candidate-audit-3.json`证明17个共享生产应用DLL在全量测试/冻结/窗口哈希一致，两个WPF DLL独立绑定最新窗口；13个OcctSharp托管DLL在SDK消费者/测试/冻结一致，62个原生DLL与SDK包审计一致。三语言各1258非空键、43个Contour键且集合一致，相关本地文档链接及diff检查通过。

物理键鼠、外部CAD以及真实供应商轮廓长会话没有在此批宣称通过。进一步[有限平面区域推拉/删除面修补](E4_PLANAR_FACE_EDITING_PLAN.md)、缺失算法历史及其余A–F继续；G仍暂缓。

D4被冻结候选2065/2065及六窗口证据保持；E3已改变当前编译输出，不能重新运行D4 finalize脚本要求旧候选与当前测试目录一致。
