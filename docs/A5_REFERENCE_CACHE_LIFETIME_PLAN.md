# A5 视口引用缓存与资产生命周期

2026-10-11 **status183 A–F ACTIVE，G暂缓。生产仍为缓存修复1000/SDK26，正式A4/B17/C5/D5/E4/F5g。合并候选1056保留原6392+新30=6422，两配置构建0/0、联合各350全部独立通过，SDK13/实际原生62绑定。已合入B18实时1/3/7层生成点链、已验G15密度显示和绘图检查缺type的只读反馈；身份先校验，不自动补type/身份。第二版348/2的上下文工具裁剪回归已通过压缩说明修复，完整12工具/8192/1024及原安全预算、schema形状和全部断言保持。完整6422串行Release/Debug已启动，尚无两配置终态。C1专用WPF两配置0/0、测试载荷17/13/62已验，窗口尚未运行。原SDK27 Bonsai工程图完成1轮/22调用后耗尽16工具轮，多文档完成7轮/43调用后第8轮空回复，两项FAIL和零资产记录保留；当前合并供应商未运行。SDK28真实原生渲染标志桥接及12测试只准备源码，未编译；WPF自动恢复、物理GPU及其余A–F缺口继续。旧6122、实际材质/G15、圆盘/窗口和原性能证据按各自载荷保留，不能当作新候选全量或当前应用验收。桌面尚未开始，键鼠保持释放。** 见[本轮实现与门禁](AF_MERGED_LIVEPOINT_DRAWING_DENSITY_PLAN.md)、[350两配置](../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/build-focused-both-3-verification.json)、[完整6422冻结](../artifacts/af-sdk27-livepoint-drawing-type-density-draft-1/full6422-preparation-1.json)和[C1构建](../artifacts/b18-live-point-window-draft-2/build-2-verification.json)。

2026-10-10 当前结果：原18实际8通过/10失败；扩展来源样式及描述格式/版本后48实际12通过/36失败。仅视口一文件接入修复，两配置构建0/0、原5909清单保持；同一48程序完整字节不变，修复后48全部通过。132整PNG为4张直阅及128完整字节复用，拒绝保持旧画面、双视口绑定/高亮/来源样式恢复及最终零资产均已验。原冻结1000性能和窗口与修复版分列；修复版Release与Debug完整各5909已通过；圆盘首次因鼠标事件失败，原条件重验7次约40–56ms、0鼠标事件通过。综合窗口另7次约35–55ms、12关闭周期与60完整画面通过；修复版direct完整5699.4681/6800.2534ms，第二轮超过原6482ms目标；生产Open67138.5414/66362.2132ms及两张完整参考图字节通过，性能未整体验收。见[两轮实际结果](../artifacts/a5-reference-cache-repair-performance-verification-2.json)。见[全量终态](../artifacts/a5-reference-cache-repair-full-both-1-exit.json)、[圆盘重验](../artifacts/a5-reference-cache-repair-radial-window-2-verification.json)和[窗口画面](../artifacts/a5-reference-cache-repair-window-images-1-verification.json)。见[原1000两配置完整5909](../artifacts/b18-guided-policy-full-both-1-exit.json)、[原1000性能](../artifacts/a5-current1000-performance-verification-1.json)、[原1000圆盘](../artifacts/a5-current1000-radial-window-1-verification.json)、[通用窗口及60图](../artifacts/a5-current1000-window-images-1-verification.json)、[修复前48](../artifacts/a5-current1000-reference-consumer-run-2-verification.json)、[修复后48](../artifacts/a5-current1000-reference-consumer-run-3-verification.json)、[132图](../artifacts/a5-reference-cache-repair-images-1-verification.json)和[原完整回归活进程](../artifacts/a5-reference-cache-repair-release-full5909-1-observation-1.json)。

合并材质/共享CLI候选仍处于独立副本：原完整5958的14项默认工具目录FAIL已保存；恢复默认12目录后的两配置各159及独立CLI30通过，完整Release6020/6020已独立通过，同冻结Debug正在串行验。当前生产1000缓存修复源码保持，合并材质、性能分阶段草稿与原供应商反馈草稿均未应用到生产；这些组件结果不改判原direct第二轮6800ms的FAIL。见[当前接续](A5_MATERIAL_AUTHORING_PLAN.md)。

## 初始准备记录（历史状态）

2026-10-09：当前1000/SDK26的两配置完整5909已经通过；[源码归档](../artifacts/b18-app-current1000-source-freeze-1.json)与[本地WPF候选](../artifacts/a5-current1000-app-build-verification-1.json)已经绑定。本文的18项消费者仍是准备状态，尚未编译或运行，不能作为缺陷复现或修复验收。A–F继续，G按用户要求暂缓。

## 需要验证的行为

同一主BRep的引用表变更必须经过该描述对应的实际解码。已显示的旧资源不能让新的损坏、外来或缺失表绕过验证；拒绝场景替换时，应保留原完整画面及资源数量。

两个视口已经接收场景后，原调用方释放全部租约，视口仍须能建立当前几何的精确选择、显示高亮、恢复完整画面。每个视口独立持有自己的资产租约和原生资源，最后关闭归零。

这些条件验证当前解码图和视口生命周期。缓存键、资产哈希、修订号、拓扑数量或索引均不证明原始捕获身份，也不证明组合历史对应。

## 已确认的源码事实

[OcctViewport](../Cadoryx.Rendering.Occt/OcctViewport.cs)的缓存键目前是主资产、XDE来源与格式，未包括成员表或完整路径表描述。资源构造器只获取主BRep租约，而后来建立精确绑定会再次通过保存的几何描述读取边界。

[GeometryAssetLease](../Cadoryx.Kernel.Abstractions/GeometryAssetLease.cs)已经支持同时持有主BRep与补充表。[源码审查记录](../artifacts/a5-current1000-material-reference-source-audit-1.json)中的热缓存及调用方释放影响最初为待复现假设；上述48已实际复现并修复，具体新证据见本文开头。

## 原生消费者

[18项准备记录](../artifacts/a5-current1000-reference-consumer-preparation-1.json)绑定当前1000来源、17个应用程序集、13个SDK程序集与62个原生DLL，消费者源码位于[Program.cs](../artifacts/a5-current1000-reference-consumer-1/Program.cs)。它使用自己创建的隐藏原生窗口，不操作桌面键鼠，也不修改既有消费者或5909测试清单。

| 检查 | 计划组合 | 数量 |
| --- | --- | --- |
| 错误补充表 | 成员表/完整路径表 × 损坏字节/外来真实表/缺失资产 × 冷/热缓存 | 12 |
| 原调用方释放 | 普通BRep/成员表/完整路径表 × 绑定尚未建立/已经建立；每项都用两个独立视口 | 6 |

每个错误表首先必须被公开几何解码器实际拒绝，才能用于检查视口。场景拒绝前后比较整张画面，不以对象数量或局部采样替代画面证明。

释放用例记录每个边界资产的可用性、两个视口的实际绑定错误、高亮后的完整画面变化、恢复后的完整画面一致性及最终资产归零。已经建立绑定的场景应分别说明“仍能使用缓存的原生绑定”和“补充表资产是否保留”，不能混称绑定失败。

计划输出60张完整480×320图像。编译成功、清单准备或源哈希相同均不代表18项运行成功。

## 修复草稿与接入顺序

[单文件修复草稿](../artifacts/a5-reference-cache-repair-draft-1/preparation-1.json)位于当前源码归档之外，未接入、编译或验收。它准备将完整补充表描述纳入缓存键，复用现有边界租约接口，并在来源样式路径声明补充表时验证实际原生解码。

1. 保留当前1000候选的性能与窗口验收证据。
2. 编译、核对并运行上述原始18项；保留最初失败及实际异常。
3. 在原断言不变的基础上补充XDE来源样式、描述格式与版本变化的冷/热缓存场景。
4. 根据实际复现接入最小修复，再验证原18项、新增场景、来源样式整图、独立选择与资源释放。
5. 完成修改后对应的应用构建、相关回归及必要候选门禁；旧候选结果只代表旧候选。

完整材料创作窗口、PBR/纹理保存重开、全部渲染/GPU故障、完整历史材料及跨作者身份组合仍需各自实现和验收，不由本文的缓存检查替代。
