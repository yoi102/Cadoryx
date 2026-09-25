# OcctSharp 对 Cadoryx 的能力核查

2026-09-24 M4-S3s–x：闭合直线孔复用锁定包的 `ShapeFactory.CreatePolygonWire`、`CreatePlanarFace`、`Shape.Extrude` 与 `Shape.Cut`。独立 NuGet 消费新增真实 20×20 mm 外形与 4×4 mm 多边形通孔，验证 10 mm 拉伸的 3,840 mm³ 体积；`artifacts/occt-capability-m4s3-polygon-holes` 为 23/23 场景、62/62 原生 DLL 与包载荷一致。草图孔环引用、严格区域校验、features v13 与建模 UI 均属 Cadoryx 应用层，本轮无需新增 OcctSharp 绑定。范围见[多边形草图通孔](POLYGON_SKETCH_HOLES.md)。

2026-09-24 M4-S3m–r：本轮的多孔勾选、既有拉伸换源、草图切线与角度约束及 `sketches` v3 均属于 Cadoryx 应用层。真实圆孔拉伸继续使用锁定 `8.0.1-preview.28.cadoryx.topology.1` 包的现有圆边、平面、拉伸与布尔接口。独立 NuGet 消费证据 `artifacts/occt-capability-m4s3-constraints` 为 22/22 场景通过、62/62 原生 DLL 与包载荷一致；没有发现本轮所需的新 OcctSharp 公共绑定缺口。

2026-09-24 M4-S3a–f：锁定的 `8.0.1-preview.28.cadoryx.topology.1` 包已直接提供 `ShapeFactory.CreateCircleEdge`、`CreateWire`、`CreatePlanarFace` 和 `Shape.Extrude`。独立 NuGet 消费探针新增真实圆边/圆面/拉伸及解析体积检查，`artifacts/occt-capability-m4s3-circle` 为 21/21 场景、62/62 原生 DLL 与包载荷匹配，无需新增 OcctSharp 绑定。Cadoryx 草图引用和文件协议仍由应用层实现，范围见 [圆形草图区域](CIRCULAR_SKETCH_PROFILES.md)。

2026-09-24 M4-S3g–l：同一锁定包的 `Shape.Cut` 与上述圆面/拉伸 API 已足够实现逐孔精确布尔切除。独立 NuGet 消费 `artifacts/occt-capability-m4s3-holes` 新增真实圆环通孔，22/22 场景、62/62 原生 DLL 与包载荷匹配。应用侧的区域有效性、稳定孔 ID 和 features v12 仍由 Cadoryx 实现；见 [圆孔拉伸](CIRCULAR_SKETCH_HOLES.md)。

2026-09-24 M4-T3-B2d 更新：先前锁定包的 `RepairSnapshot` 深拷贝无法将 Viewer 当前选中的原始子形状映射到完整拓扑表；`IsSame` 和子形状序列化不能证明对应。OcctSharp 因此新增 `RepairSnapshot.FindTopologyIndex` 手写桥，使用原始形体的 `TopExp::MapShapes`；Cadoryx 锁定本地 `.topology.1` 包。其余圆角、倒角和历史能力仍由已有公共接口提供。具体范围见 [局部结果精确拓扑](EXACT_LOCAL_TOPOLOGY.md)。下文更早的“生成拓扑待实现”只描述当时状态。

2026-09-24 M4-T3-B2c1–c4（历史阶段）：局部结果可沿唯一已验证的原 Box 边历史继续做圆角，也可使用绑定的线性变半径；当时锁定包探针 `artifacts/occt-capability-20260923-235813` 为 19/19 场景、62/62 原生 DLL 与载荷一致。

2026-09-23 M4-T3-B2 增量：Cadoryx 已接入多条原始 Box 边的一次圆角／倒角和单边线性变半径圆角，配方、窗口、features v8、真实 BRep／历史与桌面冒烟见 [ROADMAP](ROADMAP.md)。锁定 `.viewcube.5` 独立包消费 19/19 场景、62/62 原生 DLL 一致，证据 `artifacts/occt-capability-20260923-233837`。下方“待增加多选配方与 UI”等表述属于 2026-09-13 基线，不是当前进度；局部结果连续建模、生成拓扑显式重选仍待 Cadoryx 应用层实现。

2026-09-23 ViewCube 增量：旧锁定包的手写 `OcctViewer` 没有 ViewCube 显示、命中与相机方向 API；生成 `AISViewCube` 类型不足以接入 Viewer 私有的 AIS 上下文。已在 OcctSharp 手写桥补齐显示、面／边／角命中、相机方向和弧形箭头命中，Cadoryx 锁定 `8.0.1-preview.28.cadoryx.viewcube.4`。独立 NuGet 消费探针与窗口证据见 ROADMAP M2-V8。此前 M2-V5 的“没有新缺口”仅针对轮廓形体，不适用于 ViewCube。

2026-09-23 M2-V5 补充独立 NuGet 消费场景：锁定包的 `CreatePolygonWire`、`CreatePlanarFace`、`Shape.Extrude` 与 `Shape.Revolve` 生成正确体积的真实轮廓形体；18/18 场景、62/62 原生 DLL 包载荷匹配，证据在 `artifacts/occt-capability-profile-ghost`。视口本身仍由 Cadoryx 发布后窗口冒烟验证。本阶段没有证实新的上游 API 缺口，故不修改 OcctSharp。

2026-09-23 增量核查：Cadoryx 现锁定 `8.0.1-preview.28.cadoryx.gradient.1`，独立 NuGet 消费探针为 17/17 场景（原 16 组加 Viewer 渐变 API 契约），62/62 原生 DLL 与包载荷一致；证据在 `artifacts/occt-capability-20260923-181132`。真实竖直渐变由桌面窗口截图上下像素另行验收。下文 16 组与 .h2b2.2 是 2026-09-13 的历史基线。

核查日期：2026-09-13，复核：2026-09-23。结论：当前锁定的 `OcctSharp` 与 `OcctSharp.Native.win-x64` 足以支持 Cadoryx 已实现的基础建模、H2-C1 历史诊断交互，以及 T3／S3 多项待接入功能的主要内核操作。本轮没有发现需要新增上游封装才能继续下一阶段的阻塞项。完整 OCCT API 迁移仍未完成。

不能将当前 Cadoryx 的 Box 限制、多边／变半径按钮缺失、草图只接受直线轮廓，直接归因于 OcctSharp 未迁移。它们分别受 Cadoryx 的引用协议、LocalFeatureRecipe、草图轮廓与编辑器实现限制。本轮新增独立 NuGet 能力验证程序与本报告；上游已有布尔历史改动保留，没有再增加绑定或修改生成文件。

## 包的分工和版本

| 项目 | 核查结果 |
|---|---|
| OcctSharp | C# 门面及高级组合 API；通过精确版本依赖引入 12 个托管模块 |
| OcctSharp.Native.win-x64 | 同一原生桥、OCCT 及配套原生 DLL、部署目标与许可证；不包含需要“翻译为 C#”的业务类 |
| 当前版本族 | `8.0.1-preview.28.cadoryx.h2b2.2`，ABI 1.72，bridge 0.80.0，OCCT 8.0.1 |
| 实际包来源 | 相邻 OcctSharp 仓库的 `OcctSharp/artifacts/packages` 本地 NuGet 源 |
| 部署验证 | 62 个原生 DLL 均与对应 nupkg 字节哈希一致，运行时全部来自独立消费者的发布目录 |

“迁移到 C#”在本项目中是为 OCCT 提供公开、类型安全、明确所有权的 C# 调用接口；底层几何算法继续由 C++ OCCT 执行。直接引用匹配版本的两个 NuGet，已包含这一调用链。

普通 Preview.28 和本地 `.cadoryx.h2b2.2` 不是同一个产物：后者包含 H2-B2 新增的 `BooleanHistoryModeling.Build` 和对应原生导出。不能只把门面或 Native 的其中一个换回普通 Preview.28。本轮未核实公网 NuGet 发布状态。

## 能力与待接入项

| Cadoryx 功能 | 已有公开 C# 入口 | 本轮证据与应用边界 |
|---|---|---|
| 基础体、平移／旋转 | ShapeFactory、Shape.Transformed、ShapeTransform | Box、Cylinder、Sphere 的几何／体积和变换边界通过 |
| Fuse／Cut／Common 及真实历史 | BooleanHistoryModeling.Build | 三种布尔实测体积、两输入来源、实际拆分历史通过；Cadoryx 已接入 |
| 多边圆角 | ContourFilletRecipe、FilletContourProgram | 一次执行两条独立轮廓，理论体积通过；Cadoryx T3 待增加多选配方与 UI |
| 变半径圆角 | FilletContourProgram.FromLaw／Sampled、ScalarLawDefinition | 线性法则和采样法则均执行真实模拟／建模，截面半径发生变化 |
| 双距离倒角 | ContourChamferRecipe、ChamferDimensions.TwoDistances | 1／2 mm 两个距离实测体积正确；Cadoryx 当前仅等距参数 |
| 连续局部建模 | RepairSnapshot.Create(前一步结果)、ContourFilletRecipe.Build | 第二次圆角直接作用于第一步的非 Box 输出，输入释放后结果仍有效；Cadoryx 的 Box 语义引用限制需单独扩展 |
| 孔 | LocalFeatures.Hole | 真正去除材料并核对理论体积；应用尚无对应配方／窗口 |
| 圆、B-spline、交点与投影 | SketchCurve2d、SketchModeling | 样条计算点、唯一交点、投影和生成曲线边通过 |
| 带孔曲线区域拉伸 | SketchCurveChain2d、SketchProfile2d | 圆环区域及理论体积通过；Cadoryx S3 已支持完整圆孔拉伸，三点圆弧加隐式弦及单圆弧直线混合闭环已用于精确拉伸；多圆弧闭环、圆弧孔和完整约束求解仍待实现 |
| 放样、扫掠、缝合 | ShapeFactory.CreateLoft／CreatePipe／Sew | 两截面实体放样、实体扫掠和缝合通过；应用未提供完整命令／配方 |
| BRep／STEP／IGES／STL 交换 | ShapeExchange.Read*／Write* | 四格式均实际写出并重读；STL 原生读取接口已存在，Cadoryx 尚无导入入口 |
| 装配名称、颜色、共享实例、位姿 | XdeDocument、XdeLabel、XdeOccurrence | STEP 往返保留同一零件的两个实例、名称／颜色及 0／10 mm 位姿；不代表全部 PMI／面样式保真 |
| 原生参数文档的变换历史选择 | ParametricDocument.TransformSource／Select／Resolve／GetHistory | 实际平移后旧选择解析为 Resolved，并得到 Modified 关系 |
| H2-C1 诊断窗口／查询保存 | Cadoryx 既有 ITopologyHistoryResolver + MessagePack | 已由 Cadoryx 的 Db、Commands、IO、ViewModels 与 MetroWindow 实施；没有新增 native API，见 [历史诊断查询](HISTORY_QUERIES.md) |
| 草图约束求解 | Cadoryx.Sketching + MathNet.Numerics | OCCT 几何 API 不自动提供草图约束求解器；应用的约束扩展是独立任务 |

参数文档的 TNaming 选择属于其 OCAF 文档、结果修订和历史上下文。Cadoryx 使用自己的不可变 Db、BRep 资产和 MessagePack；不能直接将该选择对象写进 Cadoryx，或把原生参数文档的通过结果当作 Cadoryx 通用持久命名已经完成。H2-C2 仍需设计消费者上下文、显式重选、存储迁移及失效规则。

## 本轮独立验证

新增 [OcctCapabilityProbe](../tools/OcctCapabilityProbe/README.md)，只引用两个 NuGet，没有 Cadoryx／OcctSharp 源码项目引用，也没有额外 P/Invoke。探针会检查数值、拓扑、历史和重读结果，而不只检查类型能否找到。

```powershell
./tools/OcctCapabilityProbe/Verify.ps1
```

最终锁定还原、Release 发布、16 组场景通过，0 失败。证据：

- 最终复核日志：`artifacts/occt-capability-20260923-final.log`。
- 包与逐 DLL 哈希：`artifacts/occt-capability-20260923-122219/package-audit.json`。
- 逐场景、程序集／运行时来源：`artifacts/occt-capability-20260923-122219/evidence/result.json`。
- 原生桥 SHA256：`F719823BBB12CB19650656993217744C59FEEF54903E57951877D28605DB8F00`。
- 消费者从自身发布目录启动，PATH 只保留 Windows／.NET；移除 OCCT／CSF／CASROOT 环境配置，并核对已加载的 62 个原生依赖全部来自该目录。

2026-09-23 在版本与包哈希未变的工作区再次运行锁定还原和独立发布，16/16 场景与 62/62 原生载荷均通过。初次核查目录 `artifacts/occt-capability-20260913-223138` 保留作历史证据。

部分可复核数值：Cut 4,800 mm³、Fuse 6,208 mm³、Common 1,200 mm³；双距离倒角 1,785 mm³；双轮廓半径 0.5 圆角 1,798.390486 mm³；圆环拉伸 63π mm³；放样 140/3 mm³；扫掠 20 mm³。

本轮未修改 Cadoryx 产品代码，因此没有重复运行其上一阶段的 344 项完整测试或桌面冒烟；那些证据保留在 ROADMAP 的 H2-B3 记录。本轮也没有重跑上游全量 Release／Debug、Generator／Runtime 套件、干净再生成、完整 release-check、签名、公网发布，以及混合 DPI／长期资源／大模型门禁。独立消费者使用本机 .NET Runtime，不是全新机器验收。

## 什么情况下继续迁移上游

OcctSharp 当前 STATUS 的迁移统计仍有 61,571 条 Pending 和 32 个显式解析排除头文件；这是其已记录的库存，不是本轮重新扫描。借用型 adaptor、外部缓冲区、通用 sequence、可变拥有型输出等仍有未完成映射；另有四个 SDK 声明在当前 Release／Debug 库中无符号。这些数字不能直接衡量 Cadoryx 已规划功能的可用程度。

当前先按 [ROADMAP](ROADMAP.md) 接入已有能力。如果新的具体 CAD 操作需要公开接口尚不能表达的输入／输出、精确演化关系或批量数据，再到 OcctSharp 增补对应 C# 契约／原生桥。需要自动生成的类别修改生成规则并再生成；复杂所有权沿用手工高层封装约定，随后通过独立 NuGet 消费验证。没有符号的 SDK 方法不能靠添加 C# 声明解决。

本轮的判定范围是当前基础 CAD 和上表的真实场景。通用跨算法持久命名、所有可能的圆角几何、全部交换元数据、完整装配约束求解和大型模型性能仍需具体功能验收。
