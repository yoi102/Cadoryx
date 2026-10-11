# C2 扩展：关联平面、轴、点与精确来源

更新：2026-10-02。承接[C2平面基础](C2_ASSOCIATED_DATUMS.md)和[扩展计划](C2_EXTENDED_DATUMS_PLAN.md)。平面/轴/点、精确来源、基准轴旋转和三语言生产窗口的本地实施与联合门禁已完成：全量1013/1013、Release零警告/错误、桌面/C1–C4/D1–D3/工程图/PDF同版PASS（20261002-005501）。**这不是A–F整体完成。** G实机/安装/交付继续暂缓。

## 已实现的应用路径

- [原生解析](../Cadoryx.Kernel.Occt/OcctDatumGeometry.cs)按document/feature/revision/asset/fingerprint/kind/index/adapter核查选定特征资产，支持已被后继消费的来源。返回拥有值的解析平面、圆柱/圆锥轴、圆边中心和轴；圆锥采用两个圆截面中心求轴，不能把斜母线当轴。只解析选中的成员，几何值不用于猜测拓扑身份。
- [配方](../Cadoryx.Db/DatumConstructions.cs)：轴支持基础/独立、两点、两平面交线、平面法线和解析来源；点支持独立、偏移、中点、三平面/轴面相交和圆边中心。平面扩展任意精确平面面、独立框架、轴法线和可选点；角度面可用明确的面内基准轴旋转。原点/U方向由作者框架控制；平行、共面、重合、奇异、退化和跨零件输入拒绝。
- [联合图](../Cadoryx.Db/DatumPlanes.cs)使用稳定ID校验平面/轴/点、草图和特征，校验跨类型身份、所有权、名称、循环和深度128。点坐标/轴原点及沿轴偏移接入C1。圆周/线性阵列可用轴；点通过法线平面实际驱动孔、草图和工作平面位置。
- [事务](../Cadoryx.Commands/DatumCommands.cs)和参数重算保留一个候选/撤销项。精确来源修订变化时保留旧资产和评估几何、冻结依赖；明确重选同一基准ID可恢复模型。解除来源保留评估位置和方向。冻结缓存仍核对保留来源证据，不能以“失效”掩盖任意缓存。
- [存储](../Cadoryx.IO/DatumSections.cs)顺序增加datums v2，document v27，features仍v27，能力`cadoryx.extended-datums.1`。原始来源资产加入几何表及租约；读取时核对内嵌记录与几何表，再原生核验解析值。缺少解析内核时不能加载/保存带精确来源的文档；纯结构/设置读取保持独立。datums v1夹带新字段拒绝。旧JSON全迁移触及原64步上限，现扩为有界128步并增加65/128/129步边界检查。
- [生产窗口](../Cadoryx.wpf/Views/DatumEditorWindow.xaml)保留平面编辑，新增轴/点页签及单来源原生视口；不自动选来源。支持完整配方、独立框架、相交输入、稳定身份、解除、预览/确认/取消、generation guard及异步关闭。角度未编辑时保留原始rad值，框架未编辑时保留精确Quaternion。

## 当前证据与准确边界

独立锁定包消费44/44、62DLL匹配（`artifacts/occt-capability-20261001-225922`）；依赖仍为不可变的`8.0.1-preview.28.cadoryx.modeling.sweep.1`，原生SHA256保持D3基线。

核心及关联回归103/103；编辑器及基准/阵列回归46/46。补充孔定位、圆中心往返、最终抽壳/拔模平面后，专项及C1/C2/C3/C4/D1/D3联合131/131（`artifacts/c2-extended-hardening-tests/c2-extended-hardening.trx`）。覆盖旋转/位移圆柱圆锥、相交数值、跨类型循环、参数驱动阵列/草图/图纸、精确来源冻结/重选/解除、存储和畸形缓存、零泄漏。

第一轮三语言实际控件PASS（`artifacts/c2-extended-ui-development-1`）：原生API选面、点偏移/两点轴、配方应用、预览、解除和关闭取消；绑定日志0字节、资产归零。后续同版988门禁的三语言15张布局/原生图已全部检查（`artifacts/c2-datum-smoke-20261002-000926`），标题/按钮布局修正已生效；检查发现轴/点表头误用“平面类型”，已改为“构造类型”，接着复验。WPF布局图与原生HWND图分开保存；按钮路径为WPF Automation Invoke/Toggle；真实桌面指针尚未验证。

第一轮全量985项：983通过、2项旧JSON迁移因64步上限失败（`artifacts/c2-extended-full-tests.log`）。保留该失败记录。修复后988/988、Release零警告/错误、桌面/C1–C4/D1–D3/工程图/PDF同版PASS（发布20261002-000926），九份绑定日志0字节、资产归零；日志`artifacts/c2-extended-final-full-verification-1.log`。这是基准轴旋转前document27/features27的基线，不代表后续改动已经完成全量验收。

基准轴旋转现已接入[配方](../Cadoryx.Db/RevolveAxisReference.cs)、原生构造、跨基准依赖、C1重算、图纸更新和[生产工具](../Cadoryx.ViewModels/RevolveAxisEditor.cs)。轴保存零件局部点/单位方向和可选基准ID/修订；必须在轮廓的XZ子午面内。独立轴允许轮廓处于轴的任一侧，方向控制绕向；解析变换后按圆/圆弧极值拒绝穿轴，原生验证精确BRep及自干涉。默认轴的旧语义保持。解除保留最后评估轴线，来源变动冻结后继，明确重选同ID恢复；角度未改时保留原始rad。修正曲线冻结轮廓在生产编辑时丢失的问题，并使角度手柄围绕实际轴显示。

新增存储companion为PackRecipe/PackRecipeV3 Key18，当前实现document28/features28、datums2，能力`cadoryx.revolve-axis.1`；顺序迁移保留旧输出版本，旧schema夹带、其他配方夹带、错误方向/缓存拒绝。新旋转及相关回归88/88（`artifacts/c2-revolve-axis-tests/c2-revolve-axis-focused-3.trx`）；首轮新增测试中一个混合轮廓样件违反原三边契约，修正样件后通过。旋转专项16项和数值/生命周期边界9项联合25/25；最后旋转/扩展基准/参数/面特征联合116/116（`c2-revolve-hardening-1.trx`）。深度128允许、129拒绝且与字典顺序无关；相交覆盖旋转框架及.001/1/10000尺度；近平行拒绝；真实原生选择晚到时generation/关闭保护不发表旧结果，等待资产租约释放。

最终门禁日志`artifacts/c2-revolve-final-full-verification-1.log`，发布`artifacts/publish/20261002-005501`；九份绑定日志0字节、关闭资产归零。三语言实际控件包含解析圆柱轴/圆中心、生产旋转轴选择/解除/取消/确认、精确半环体积和单次撤销，以及来源改变冻结后明确重选同ID恢复旋转、保存/重开。最终C2的25张布局和原生HWND图全部逐张检查：构造类型表头、坐标显示、轴/点页隐藏平面控件已生效；平面编辑长表单由滚动区域承载。WPF按钮为Automation Invoke/Toggle，原生选面为Viewport API；真实桌面指针仍未验证。审计见`artifacts/c2-revolve-final-verification.json`。历史失败证据保持。

继续[C3特征重放/装配实例](C3_MIRROR_AND_PATTERNS.md)、B剩余曲线/区域、D任意曲面拉伸终止、E历史/拓扑/装配与F制造/交换/命令；A交互和固定样本性能不删减。旋转的Bézier/样条和孔岛范围仍归B/G06，不能由C2完成推定已实现。
