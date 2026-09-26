# M6：审阅与交付七个大阶段

本批接续 M6_WORKSPACE_TOOLS，不重复历史预算、实例搜索及包围盒测量。七项实现完成后统一执行验证，以下结果区只记录本批实际证据。

## 1. 精确 BRep 检查与两体最小距离

属性面板新增“精确几何检查”。明确选择 1–256 个体后计算面积、面/边/实体数；单一实体提供体积及体积形心，两个体额外提供最小 BRep 距离及一对世界坐标最近点。复用固定 NuGet 的 `InspectProperties`、`DistanceTo`，不从包围盒推断精确距离。

输入验证文档、精确实例路径、体与几何修订，捕获文档资产租约后进入内核串行队列。结果到达时再次检查请求序号、快照状态及取消。选择/文档变更、关闭会丢弃陈旧结果。取消可中止排队及后续步骤，不能打断已经执行的单次 OCCT 算法。

结果按文档单位和精度显示。体积合计包括重叠实例；形心按均匀密度，并非材料加权重心。复合体/片体/线体不报告实体体积；零最小距离不表示穿透量。多个等距解只展示一对点，不把它用作拓扑绑定。

## 2. 世界平面与薄层剖切

属性面板“剖切视图”支持 X/Y/Z 法向、世界偏移、反向和中心薄层厚度，应用/清除命令。两个原生视口共享当前文档会话的显示剖切。平面资源先建立成功再替换，关闭视口前释放。

这是视觉裁剪：不写入几何、不改变导出、不生成剖面线或封口、不进入撤销栈；关闭文档后不保存。本七阶段批次尚未包含几何截面；后续已补充独立的冻结截面曲线命令，见[截面、干涉与缓存生命周期](M6_SECTION_INTERFERENCE_CACHE.md)。剖面工程图和持久审阅场景仍未完成。

## 3. 实例隔离、隐藏与聚焦

隔离选择、临时隐藏及恢复显示现在位于视口右键松开菜单，聚焦位于 MainRibbonView 的视图页（原工具栏入口已整理，见[视图导航](VIEW_NAVIGATION.md)）。共享零件的不同实例按 `(OccurrencePath, BodyId)` 区分；选中装配节点可作用于该路径下当前可见体。过滤建立在文档原有可见场景上，“恢复显示”不会把图层/文档隐藏内容强制打开。导出及保存不受临时显示过滤影响。

聚焦按变换后的八角包围盒计算相机目标，包含视口宽高比，并复用相机动画。显示过滤在当前文档会话内保留；删除的路径不会自动绑定到别的实例。

## 4. 双原生视口

每个文档可开启左右两个 `ViewportPane`，独立 HWND/OCCT Viewer、相机、ViewCube、网格显示尺度及动画，共享模型、预览、选择、隔离和剖切。适应、聚焦、标准方向和双视口开关统一位于 MainRibbonView 的视图页，导航作用于最近激活的视口。关闭第二视口会立即释放原生资源，重新开启恢复其相机。原有浮动/停靠和文档关闭生命周期继续适用。

第二视口会增加原生/GPU显示占用，未宣称零额外开销或多GPU优化。双视口布局与相机仅为会话状态。

## 5. 独立命令行转换与检查

新增 `Cadoryx.Cli`（net10.0/x64，不启动 WPF），随解决方案构建：

```powershell
Cadoryx.Cli.exe inspect part.cadoryx --exact --output inspection.json
Cadoryx.Cli.exe convert part.cadoryx part.step
Cadoryx.Cli.exe convert part.step part.cadoryx
Cadoryx.Cli.exe convert part.cadoryx part.stl --linear 0.05 --angular 0.3
```

输入支持 CADORYX/STEP/IGES/STL，输出同样支持。转换复用现有 MessagePack 迁移和 OCCT 交换路径，保留各格式固有能力边界。STL 输入使用现有 `ShapeExchange.ReadStl`，坐标按毫米解释并产生 `StlUnitless` 诊断，保留三角面，不恢复解析曲面、装配或闭合实体。默认只导出文档可见体，`--all` 包括隐藏体。写入采用同目录临时文件，成功后替换；存在目标时需显式 `--overwrite`，输入输出同路径拒绝。参数重复、未知选项、非有限数、错误扩展名均拒绝。

退出码：0 成功，1 操作失败，2 参数错误，130 取消。Ctrl+C 取消后不提交输出。供程序读取时使用 `--output` 的 JSON 文件；原生交换器可能向控制台输出自身日志。当前是单文件命令，可由外部脚本循环批处理。

## 6. 磁盘资产与按需字节读取

`DiskAssetStore` 实现既有内容寻址/租约契约。Stage 将字节写入本次进程独立目录，重复哈希共用文件。Acquire 只增加引用，Length 不读取内容；读取 Content 时校验长度及 SHA-256，在该消费者租约内缓存字节。最后一个租约归零删除文件，Dispose 不破坏尚在工作的消费者。

WPF 和 CLI 默认使用磁盘资产。WPF 可通过 `CADORYX_ASSET_STORE=memory` 回退内存实现；CLI 用 `--memory`。历史预算及规模统计使用租约长度，避免为了统计把历史几何全部读回内存。资产统计接口用于既有零泄漏门禁。

边界：ZIP 加载仍逐项解压、校验和 Stage；这是磁盘驻留及使用时读取，不是直接随机访问 ZIP 中未校验的资产。显示/算法消费者仍需完整 BRep 字节，未引入内存映射、跨进程资产共享或严格进程内存上限。异常终止可能遗留独立缓存目录，恢复文档仍以恢复存储为准；自动孤儿清理另列后续工作。

## 7. 可复现性能报告

```powershell
Cadoryx.Cli.exe benchmark assembly.step --iterations 3 --output disk.json
Cadoryx.Cli.exe benchmark assembly.step --iterations 3 --memory --output memory.json
```

报告包含输入 SHA-256/字节数、记录时间、OS/架构/.NET/CPU信息、实际锁定内核包版本、资产模式；每轮记录载入、场景构建、首个 BRep 读取、校验保存时间、实例规模、唯一资产字节、磁盘读取次数、托管分配增量、私有内存前后值及进程峰值工作集。迭代数限制为 1–20，输入前后哈希不一致拒绝报告。

第1轮包含冷启动，后续可能命中OS/原生缓存，不强制GC；峰值工作集是进程生命周期值。此工具没有 Viewer，因此不报告首帧/GPU/交互延迟，也不把小样本通过当成大型工业模型性能承诺。

## 本批统一验证

2026-09-26 七项实现后统一验收，最终整套命令为：

```powershell
pwsh -NoProfile -File scripts/verify.ps1 -PublishSmoke -WindowSmoke -SketchSmoke -RecoverySmoke
```

- PASS：锁定还原、Release 构建 0 警告/错误；499/499 测试，0 失败/跳过。本批新增 24 项用例，覆盖世界位姿/最小距离、片体无体积、请求限额/取消/陈旧选择、磁盘去重/完整性/并发/历史存储/释放、剖切平面、精确实例过滤/聚焦、CLI 三格式往返检查/输出保护/基准报告。日志 `artifacts/m6-seven-acceptance-v5.log`。
- PASS：独立固定 NuGet 消费者 29/29；62 个原生 DLL 哈希与 nupkg 一致，实际载入路径来自发布目录。日志 `artifacts/m6-seven-capability-final.log`，证据 `artifacts/m6-seven-capability-final`。面积检查使用包所支持的面/壳输入，因此在应用适配层逐面累加；本批不需修改或迁移 OcctSharp。
- PASS：独立 CLI 发布目录（受限 PATH），检查 JSON、STEP/IGES/STL 导出、覆盖保护、STEP 重新导入、磁盘/内存两种模式各两轮基准。证据 `artifacts/cli-20260926-205842`。报告样本为 3,694 字节固定 box 文件，仅证明报告与流程，不证明真实大型装配性能。
- PASS：最终发布目录 `artifacts/publish/20260926-205849`；常规、窗口、草图、恢复四组证据分别位于 `artifacts/smoke-20260926-205849`、`artifacts/window-smoke-20260926-205849`、`artifacts/sketch-editor-smoke-20260926-205849`、`artifacts/recovery-smoke-20260926-205849`，全部 result.txt 为 PASS；五份绑定日志均 0 字节。
- PASS：新增真实窗口审阅检查见常规证据内 `review-tools-result.txt`：精确测量绑定、双原生相机独立、实例隔离/聚焦、半空间/薄层像素变化、清除剖切、关闭/重开第二视口相机连续性、文档状态不变。已查看 `review-section.png` 和 `review-slab.png`，薄层显示两实体的侧面切片，不冒充封口几何。
- PASS：既有 12 轮浮动/停靠、三语言窗口、草图物理指针、三格式导出及生产30秒定时快照后终止/重启恢复；正常关闭后原生宿主/资产归零。487 个三语言资源键集合一致，XAML 引用无缺键，diff 检查通过。

前期构建适配问题、直接传实体给面积接口、默认空扩展集合和 STL 导入入口缺口的失败日志保留在 v1–v4 与初次能力检查记录中，不当作通过证据。最终 v5 才是整套通过记录。实现期间没有逐阶段运行测试。

本批未覆盖的冻结几何截面和新协议缓存崩溃回收，已在后续[三个大阶段](M6_SECTION_INTERFERENCE_CACHE.md)补充。仍需：真实大型模型分档基准、渲染/交互计时、多视口物理输入及混合DPI/RDP/长时资源、关联截面/封口、工程标注、完全按需ZIP存储和旧版缓存迁移。后续工作不计入本七阶段批次的原始验收范围。
