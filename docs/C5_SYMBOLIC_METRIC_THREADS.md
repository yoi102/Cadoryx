# C5 公制符号螺纹孔实施记录

2026-10-04。继续 [A–F](AF_IMPLEMENTATION_TRACKER.md) 的 C/G11 和 F/G20；[螺纹实施契约](C5_THREADED_HOLES_PLAN.md)分别验收符号表达与实体牙型。G 第七批暂缓。本记录覆盖符号路径，不能作为 C5 或整个 A–F 全部完成的依据。

## 作者规格与精确底孔

`MetricThreadSpec`独立保存公称直径、实际螺距、旋向、通/盲模式及有效螺纹深度；`metric-coarse-thread-pilot-common-v1`明确列举 M3/4/5/6/8/10/12/16 粗牙和 workshop 底孔尺寸。这是版本化常用选择，不是完整 ISO/GB 标准合规认证。公称直径不冒充钻孔直径，螺纹深度不冒充钻孔深度。

首批仅 Plain 孔、与底孔一致的通/盲模式、Right/Left 旋向及 Symbolic 表达。盲孔有效螺纹深度不得超过底孔深度，螺纹与 clearance 规格互斥；沉孔/沉头组合、不同通/盲模式和 `Modeled` 均明确拒绝。几何仍为精确圆柱底孔，没有螺旋牙型。

孔编辑器已接入三语言规格、旋向、有效深度及螺距提示、预览/取消/确认、编辑和撤销/重做。启用时锁定底孔直径并禁用冲突字段，不静默清除既有不兼容规格；明确解除时保留底孔几何。盲孔 `ThreadDepth` 可通过 C1 参数驱动，存在该驱动时不能直接解除规格。结构化 hole 作者和只读配方使用同一严格契约，未知字段/规格、错误 pitch、null、非法表达或 clearance 冲突均拒绝。

## 几何身份与制造证明

仅改变符号规格时，整个底孔/孔阵列几何配方除 Thread 外必须完全相等，才保留原始 BRep 与历史；任何真实几何变化仍重建和验证。参数重算同样使用原始几何基线。此规则修复了最初“旋向/深度改动重建 BRep 导致原始图纸附着失效”的实际失败，没有用距离或遍历索引猜测重绑。

孔表只有在当前直接作者孔与独立原生直径、钻孔深度、入口、方向及孔型全部吻合时，才携带螺纹语义。导入孔、阵列或无法证明作者来源的后继不自动得到螺纹标注。Update 在仍有效的同一个原始 attachment 上独立重验几何，再刷新作者语义；手工 Put、保存及重开仍要求完整 SameProof，不能伪造旋向或有效深度。

孔表显示例如 `M6 x 1 LH · L 5 · 符号`，底孔 5 mm 和钻孔 8 mm 分列。原生文档保存规格；STEP/IGES/STL 的 `EXPORT.THREAD_INTENT` 与实测交换报告说明符号语义丢失，并明确没有实体牙型。

## 存储与迁移

格式为 document47/features40/drawings13；sketches18/history4 保持。必需能力 `cadoryx.thread-hole-intent.1`。B13 的 document45→46、C3 features38→39 和 F3h drawings11→12 均保持固定独立迁移，再分别迁移至新格式。旧 features39（包括嵌套配方及孔阵列伴随字段）和 drawings12 夹带 thread proof 明确拒绝；null 表示没有螺纹，普通旧孔不凭空获得新规格。

## 当前验证

| 检查 | 证据 |
|---|---|
| 最终联合 | `artifacts/c5-joint-1/results.trx`：304/304，包含最终 35 新例、G2、孔/阵列、图纸、参数、存储/迁移、模型作者和十二工具预算 |
| Release | `artifacts/c5-build-11.log`：整个方案 0 警告/0 错误；隔离 c5-build-1 输出 |
| 冻结窗口 | c5-thread-20261004-1 的 C5/C4/B13/C1/E1/desktop 六门禁 PASS，绑定日志空，关闭后资产归零；三语言真实控件及 3 个原生底孔证明 |
| SDK 独立消费者 | `artifacts/c5-sdk-2/result.json`：八规格×通/盲 16/16，原生体积、入口、直径、深度；只引用锁定 .10 包，无 Cadoryx DLL |
| 同冻结 DLL 图纸消费者 | `artifacts/c5-drawing-3/result.json`：引用 8 个冻结生产 DLL，不重编应用；三语言文件重开、LH 编辑、原 BRep 保持、原生 hole proof、孔表/保存/重开及矢量 PDF PASS |
| 审阅 | `artifacts/c5-review-1/review.json`：12 张控件截图与 3 页矢量 PDF 全部直接审阅；字形和孔表可读，没有标题栏重叠。详细领域异常仍为英文；截图不证明原生 HWND 或物理输入 |
| 二进制审计 | `artifacts/c5-binary-audit-1.json`：17 共享应用/2 WPF/13 SDK 托管/62 原生/14 包一致，两个消费者的实际加载路径与哈希一致，三语言 1294 键相同 |
| 最终全量 | c5-full-1/results.trx：2352通过/12失败，随后因长时间无进展被中止；六孔表no-op和六drawings12版本断言失败保留，当前候选重新验证 |

保留失败：focused-1 36/38、focused-2 37/38 的精确附着失效及首次 STEP occurrence 夹具层级错误；sdk-1 编译误用不存在的 GpTrsf.Translation；drawing-2 孔表超出预留标题栏，drawing-3 改为合法位置。B13 旧 full-1 为 2333/2334，新增 G2 后十二工具预算遗漏 cad_datum，已精简重复说明，验收要求保持；最终全量须由当前集成候选补齐，不能声称旧 B13 候选曾全量通过。

## 实体螺纹继续

.10 元数据 inventory 证明没有直接接收 Geom2dCurve/GeomSurface 的公开 Shape 构边入口（`TYPED_PCURVE_EDGE_API_MISSING`），但该结论不排除其他公开作者路径。源码复核发现 `SurfaceModeling.CreateAnalyticFace/LiftCurve` 可接收解析圆柱与 UV 直线，下一步先独立验证螺旋 pcurve、三维精度/公差、扫掠和 Boolean；证明实际缺口后才补上游。未验证的采样折线或拟合不能作为精确实体牙型。

其余 A 性能/输入、B 非端点自由曲线关系/偏移连接、D 曲面终止、E 曲面移动/替换及缺失历史、F 制造规则/外部元数据/供应商失败仍继续；G 暂缓。
