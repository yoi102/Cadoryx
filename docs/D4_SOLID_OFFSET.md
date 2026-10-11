# D4：整体实体偏移实施记录

更新：2026-10-04。**当前本地契约实现及验证完成：37专项、178联合、最终2065/2065全量，Release零警告/错误，同冻结六窗口PASS。** A–F 总目标仍进行中，G 第七批按用户要求暂缓。契约见 [计划](D4_SOLID_OFFSET_PLAN.md)，审计 `artifacts/d4-offset-verification-1.json`。

## 实现

[SolidOffsetRecipe](../Cadoryx.Db/SolidOffsetRecipe.cs) 保存单一完整实体来源和带符号距离。[创建命令](../Cadoryx.Commands/SolidOffsetCommands.cs) 只消费当前精确显示输出，保留零件、实体色、材料、图层和可见性，清除无法证明继承的逐面样式标志。[原生几何](../Cadoryx.Kernel.Occt/OcctSolidOffsetGeometry.cs) 使用锁定 SDK `Shape.Offset`，核对源和结果的 exact 有效性、单实体、自干涉、体积变化和包含关系，不生成猜测历史。

Distance 已加入 C1 单位长度参数；有效完整源改变自动重算，抑制恢复、编辑精确来源和原子失败纳入已有依赖链。对原始拓扑延续返回 `HISTORY.UNSUPPORTED`，对逐面样式返回 `STYLE.UNSUPPORTED`。最终边可明确选择做局部后继，源再变后保留旧后继结果并标失效。

[存储](../Cadoryx.IO/SolidOffsetSections.cs) 追加 `solid-offset` 配方：一个源和一个 signed number，拒绝隐藏 companion。document40/features36，能力 `cadoryx.solid-offset.1`，旧 document38→39/features34→35 迁移保持独立，新增39→40/35→36；旧 schema 夹带新配方拒绝。

[生产编辑器](../Cadoryx.ViewModels/FaceFeatureEditorViewModel.cs) 和 [窗口](../Cadoryx.wpf/Views/FaceFeatureEditorWindow.xaml) 接入 Ribbon/树编辑；整体源、带符号控件、预览/确认/取消/关闭，不启用逐面选择。`cad_model` 增加作者类型 `solid_offset`，精确输入和可复用的无变化编辑回执；作者类型为13，工具总数仍为12。

## 当前证据

- SDK：`artifacts/d4-offset-sdk-2/evidence/result.json` 和 `package-audit.json`，9/9、62 包内 DLL 哈希匹配且62个实际加载来源属于独立发布目录。盒/圆柱/球内外解析体积、源指纹不变、exact 自干涉、包含关系和 STEP 重读；三项塌缩拒绝。首次误用 `WriteBrep` 编译失败保留，随后改用 `ShapeExchange.WriteBrep`。
- 应用初版：`artifacts/d4-offset-build-1.log` Release 0 警告/0 错误；测试误用命令名和缺 using 的 build-2 失败保留，build-3 通过；`artifacts/d4-offset-focused-1/results.trx` 167/167。它早于最终 UI 可见性/Hint/枚举检查和补样本，不作为最终候选证据。
- 最终构建 `artifacts/d4-offset-build-6.log` Release 0/0；`d4-offset-hardening-2/results.trx` **37/37**，`d4-offset-focused-3/results.trx` **178/178**，`d4-offset-full-1/results.trx` **2065/2065**，0失败/跳过、10m41s。build-4 新测试枚举歧义和初始孔样本失败保留，F5g 的2028项仍只属旧候选。
- 冻结 `artifacts/publish/d4-offset-20261004-1` 的 D4/D3/C1/D2/desktop/drawing 六窗口PASS，六份绑定日志空，窗口关闭资产归零；`artifacts/d4-offset-windows-1-summary.json`。六张三语言布局/原生预览逐张检查，布局截图不包含HWND原生像素，单独原生截图可见完整实体；`artifacts/d4-offset-images-1.json`。资源三语言各1215非空键且键集合相同。
- `artifacts/d4-offset-candidate-audit-1.json` 核对15个应用DLL在测试/冻结发布/窗口哈希一致，62个原生DLL在SDK包审计/测试/冻结发布一致；WPF独立绑定窗口证据。这里没有真实供应商偏移长会话或外部CAD声明。

## 单实体包装与补样本

孔样本首次揭示布尔结果外层为Compound；源exact有效且selfClear，并非坏几何。现只提取一个真实原生实体，完整核对所有下级拓扑的唯一数/出现数，额外面、边、点、重复实体或多实体均拒绝。SDK摘要会额外计数根，源Compound中的实体出现数必须先等于1；比较下级出现数时去掉根项，不能把重复成员当空包装。保留原始两项失败、诊断和修复后37项结果；没有放宽exact/自干涉或按几何猜测提取。

补样本证明旋转盒、圆柱内外、贯通孔凹实体内外、C1驱动工程图和原生/STEP重开、带逐面色导入的整体颜色/材料/图层保留及样式不继承、旧来源/锁层拒绝、最终明确选边圆角和修改后冻结。盒10×12×15向内1体积1040，向内2体积528；参数塌缩失败保原快照/历史/资产。异步取消/关闭/换代晚到候选均丢弃。

不得把基础样本通过宣称为任意实体偏移都成功；不宣称原始拓扑自动延续、完整逐面色继承、外部 CAD 或物理指针已验收。F5g 原孔表多图纸失败、未证明根因及 LM Studio 长会话失败仍保留在其独立记录中。

接着 [E3复杂边链及半径规律](E3_CONTOUR_LOCAL_EDITING_PLAN.md) 应用实现；锁定SDK10/10和62 DLL只作前提，不能提前标为E3完成。
