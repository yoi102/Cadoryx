# D3：抽壳与拔模实施记录

更新：2026-10-01。**D3 本地限定范围实现与最终联合门禁完成：全量966/966，Release零警告/错误，桌面/C1–C4/D1–D3/工程图/PDF同版PASS。** 全部 A–F 继续进行，G 按用户要求暂缓。契约见 [计划](D3_SHELL_DRAFT_PLAN.md)。

## 当前实现

- [配方](../Cadoryx.Db/FaceFeatureRecipes.cs)：抽壳保存明确去除面、正壁厚和内外偏移；拔模每面保存稳定程序 ID、独立带符号角度、中性平面、拉拔方向、传播策略及可选 C2 基准。选择记录完整 document/feature/revision/asset/fingerprint/kind/index/adapter，必须属于同一精确实体。空、重复、外来、失效面及非法尺寸拒绝。
- [原生几何](../Cadoryx.Kernel.Occt/OcctFaceFeatureGeometry.cs)：抽壳从私有源的真实成员面通过原生身份取得选定面，不能把深复制面冒充成员。检查 exact BRep、一个实体、有限正体积、原生全局自干涉，以及实际移除材料与偏移方向。拔模先 Preflight，再 Build；检查原生完成/有效性及独立质量。
- [事务命令](../Cadoryx.Commands/FaceFeatureCommands.cs)：只消费所选当前输出，保留零件和展示元数据；预览/失败/取消不改文档，单步撤销重做。上游修订改变时保存旧结果并失效后继；明确重新选面后可原子恢复建模链，不复用旧槽位。
- C1 壁厚和逐面角度字段已接入；角度字段使用稳定程序 ID，不依赖列表顺序。C2 中性平面加入联合依赖图，基准改变驱动候选重算；直接修改同一受驱动平面必须先解除基准。失效抽壳/拔模仍保留可读取的**作者尺寸**，保持已有参数表可验证；这是设计值读取，不表示冻结几何已按新尺寸更新，面重选前不能恢复有效结果。
- [存储](../Cadoryx.IO/FaceFeatureSections.cs)：追加 PackFaceFeature Key(17)，明确面列表与逐面程序 DTO，document **v26** / features **v27**，能力 `cadoryx.face-features.1`。D1 features24→25、D2 25→26 迁移保持独立；新增26→27旧版夹带拒绝。拒绝隐藏 companion、错误 kind、空面和非法方向，保存重开后可再次修改尺寸。
- 抽壳没有原始到最终历史，不伪造映射。拔模只保存经 native/BRep 往返验证的结果历史；未映射、分裂、合并和多路径仍停止。正负双面拔模样本的底部边虽然几何未变，原生历史仍未映射，查询返回 Unsupported/Target=null；不能靠坐标一致补造身份。

## 当前证据

锁定 `.cadoryx.modeling.sweep.1` 消费探针 **43/43**，**62 DLL** 哈希匹配，`artifacts/occt-capability-20261001-214543`。抽壳去顶面 10×12×15、内壁厚1体积 **680**；去顶底两面体积 **600**。逐面正负0.05/0.1 rad 拔模体积符合实际梯形公式，源保持1800。

[专项](../Cadoryx.Tests/FaceFeatureTests.cs) **22/22**：内/外和单/多去除面、正负角、多面程序、源修改冻结及后继/重选恢复、单步撤销、参数与基准、作者尺寸保留、原生文件往返/再编辑/迁移、空/重复/外来/修订/指纹/类型/过厚反例，以及畸形字段零泄漏。联合 D2、文件演化、C1/C2 检查 **112/112**，TRX `artifacts/d3-core-tests/d3-integrated.trx`。Release核心构建零警告/错误；核心阶段完整942/942、Release零警告/错误、桌面/C1–C4/D1/D2/工程图/PDF同版PASS（20261001-221749），八份绑定日志空，资产归零，日志 `artifacts/d3-core-full-verification.log`。这仍是未加入D3窗口/补样本的历史构建。

## 生产窗口与后续补样本

[编辑器](../Cadoryx.ViewModels/FaceFeatureEditorViewModel.cs)与[窗口](../Cadoryx.wpf/Views/FaceFeatureEditorWindow.xaml)已接入 Ribbon 和特征树。新建不自动选择来源或面；单源视口明确点选，多去除面、逐面带符号角度/拉拔方向/中性平面/传播可编辑。旧配方保留每个面和程序ID；失效拔模可以逐程序明确替换面而保留角度参数身份，必须重选全部旧面后才能恢复。基准关联锁定中性帧，明确解除保持精确Quaternion及位置。预览输入变化、文档换代、取消、异步关闭和晚到原生结果都释放候选，确认只提交一个历史项。

视口选面验证发现渲染三角网格会改变BRep序列化指纹。精确选择现在展示私有绑定源，使用展示前记录的来源指纹；通过原生 FindTopologyIndex 验证所选子形状是该展示源的成员，再核查完整类型表。没有使用形状距离、遍历数组次序或几何最近匹配。链式语义/精确高亮也从实际展示源取成员，避免把另一份复制形状传给原生高亮接口。

D3核心补到 **34/34**，编辑器 **12/12**，合计 **46/46**（`artifacts/d3-hardening-tests/d3-hardening.trx`）。与局部编辑及D2编辑器联合 **126/126**（`artifacts/d3-complete-tests/d3-complete.trx`）。追加圆柱抽壳体积196π和圆柱拔模锥台体积、TangentChain、零方向/垂直中性面/重复/90度/切穿/平行目标拒绝、最终精确边圆角和修改后冻结、C1驱动关联图纸/PDF与STEP原生往返。STEP结果为本机同内核往返，外部CAD证据另列。

三语言生产窗口 **PASS**，最终证据 `artifacts/d3-face-feature-smoke-20261001-225615`；实际厚度/内外、多去除面/删除清空、正负角、传播、拉拔、中性面平移/旋转、基准关联/解除、逐面替换、预览/确认/取消按钮，精确体积/失败/关闭、完整树编辑及存储验证；绑定日志空、资产归零。WPF布局截图与原生HWND视口截图分别保存，前者不能包含原生像素；12张布局/原生图片全部已逐张复查。资源三语言各918个非空键，全部XAML静态资源键存在。

首轮完整966/966、Release零警告/错误（`artifacts/d3-final-full-verification.log`），之后桌面联合检查发现链式局部高亮成员不属于切换后的展示源，已修正，不宣称该轮桌面PASS。修复及最后控件补样本后的最终全量966/966、Release零警告/错误，桌面/C1–C4/D1–D3/工程图/PDF同版PASS（发布20261001-225615；`artifacts/d3-final-full-verification-2.log`及`artifacts/d3-final-verification.json`）。九份绑定日志均0字节，资产归零。界面证据为WPF Automation Invoke/Toggle和原生视口API选择；真实桌面指针尚未验证。document v26/features v27、锁定sweep.1包及原生SHA256保持不变。整个A–F保持进行中；外部CAD和硬件证据分列；第七批继续暂缓。下一批[C2扩展](C2_EXTENDED_DATUMS_PLAN.md)目前仅计划与44项锁定消费证明，应用扩展尚未实现。
