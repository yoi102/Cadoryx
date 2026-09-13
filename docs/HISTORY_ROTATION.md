# M4-T1-H2-A：旋转历史校验与布尔能力门禁

H2-B2 已通过本地 NuGet 补齐真实布尔逐源映射，见 [布尔历史](BOOLEAN_HISTORY.md)。以下保留 H2-A 阶段协议与当时的上游接口缺口；H2-B1 文件协议见 [多输入历史](MULTI_INPUT_HISTORY.md)。

2026-09-13。本批完成 H2 的旋转表示校验子项，继续使用 OcctSharp 8.0.1-preview.26。H2 的真实合并映射、布尔逐源映射和多步传播尚未完成，不将这些能力记为已开放。

## 原因和实现

H1 对每个 full topology 槽位的子形状做 BRep 写读规范化，再比较精确指纹。旋转模型中的圆弧坐标系经过 OCCT 重建会有单位轴舍入差异，例如 `2.7755575615628907e-17` 与 `0`，以及约一个至数个 double 尾数位的变化。重复写读不保证两侧收敛到相同文本，因此不能依赖“多重写几次”。

新 `BrepDirectionRoundtrip` 只用于同次算法结果与其实际存储重载图的逐槽验证：

- 保留全图数量、类型、方向和父级索引的精确检查。
- 只识别 BRep V3 的单行圆／椭圆曲线、平面／圆柱曲面的三个单位轴字段。行类型和字段数量必须符合白名单。
- 单位轴每分量差异最多 `8 × 2^-52`（约 `1.78e-15`），两端向量模平方距 1 不超过该界限的四倍。这是无量纲轴重建误差界限，不是 mm 建模公差。
- 原点、半径、尺寸、变换、控制点、拓扑连接与其他字段继续要求精确 token 相等，不进行四舍五入、就近搜索或候选重排。
- 未知／多行曲线记录关闭该节的方向容差处理；不能把样条控制点行误识别为圆的坐标系。其他节仍做精确比较。
- 不满足上述条件时继续返回无历史的合法几何，解析为 Unsupported。新校验不会改写结果 BRep 或用户模型。

这个函数不是通用几何相等判定，不能拿两份任意模型调用它进行拓扑重绑定。它只在来源、算法和逐槽对应已限定的 BRep 写读链中使用。源／结果资产 hash、持久化 RepairSnapshot 指纹及解析时修订检查仍完全精确。

新证据的 AdapterVersion 为 `OcctSharp 8.0.1-preview.26 / full-topology-brep-axis-v2`。索引空间仍是 full topology map；Db SchemaVersion 1 和八节协议不变。解析器同时接受旧 `full-topology-brep-v1`，保存旧文件不改写其来源版本。只有显式重算产生新版本证据；撤销恢复旧证据。

## 本批证明的范围

原先旋转轴 `(2,3,1)`、角度 `0.8`、位移 `(4,5,6)` 的十二边 × 圆角／倒角共 24 项现在全部要求非空历史，同时保持理论体积与 BRep 合法性断言。旋转源上连续三次尺寸重算、精确撤销／重做、八节往返和重新解析加入原闭环测试。

额外使用旋转轴 `(1,2,3)`、位移 `(-40,50,-60)`：0.2／1.3 弧度圆角及 2.1 弧度倒角通过；3.0 弧度倒角仍不能通过限定校验，保留测试证明合法几何能保存重开而传播仍为 Unsupported。当前不承诺所有旋转、所有尺寸或一般曲面都能传播。

反向测试包含：原点或半径只改变一个很小的可表示数仍拒绝，方向差异 `1e-12` 拒绝，非单位向量、NaN、未知版本、未知节、样条载荷和拓扑数量变化拒绝。

冻结 H1 文件 `Cadoryx.Tests/Fixtures/Storage/m4t1h1-history.cadoryx`，来源 `artifacts/smoke-20260913-102750/history-Fillet.cadoryx`，SHA-256：`b007b5576dac2622911eeac9e224278795be605494bccc6d7014842048f4dad7`。真实旧版本证据可直接读取／解析、保存重开，显式重算升级适配器版本，撤销恢复原快照。此前八份样本不重新生成。

## 布尔能力核查与 H2-B

已在当前 NuGet 实际调用 `Shape.CutWithHistory(tool, ShapeKind.Face)`：10×20×30 长方体被 2×22×32 的中间刀体切开，结果有效、体积 4800 mm³，ModifiedResultCount 大于 ModifiedSourceCount，证明有真实源面拆分。但返回类型是 `BooleanHistorySummary`，仅有两侧计数，无法回答哪一面去了哪个结果槽位。

同样实测 `FeatureModeling.Boolean(Cut, [box], [tool])`，其 `FeatureHistoryItem.SourceIndex` 是输入体序号 0／1，未提供源面 full topology index、结果原图 index 或独立 Unchanged／Deleted 关系。不能把复制出来的结果 Shape 用几何相近程度配回原图。

因此 H2-A 阶段 BooleanCommand 不附带 TopologyHistory；H2-B1 已让命令接收内核的可选证据，但生产内核仍不提供布尔映射，真实切割后的 TraceAsync 明确返回 Unsupported。真实合并的逐源对应尚无生产者证明，多步传播和跨特征用户引用继续关闭。基础布尔运算本身正常可用。

H2-B2 所需上游契约：在同一次 Boolean 构建中提供输入参数序号、该输入的源 full topology index／类型、演化关系、当前最终输出 full topology index／类型；包含未改变、删除、未映射，保留一对多和多对一，记录输入图复制和结果图的对应。还需证明非破坏构建、每项所有权、结果有效性和 BRep 往返。H2-B1 已具备多输入持久化协议；拿到经验证的 NuGet 能力后再接入真实证据与传播，不通过修改 Cadoryx 的猜测算法替代此契约。

## 桌面验证

HistorySmokeRunner 改用旋转源，验证两种局部算法重算、重开、三格式导出、精确历史和资产释放。RecoverySmokeRunner 的局部源也改为旋转放置，验证生产 30 秒快照和强制终止恢复后的几何、证据字段及历史面定位。窗口风格没有变化。

最终执行结果见 [ROADMAP](ROADMAP.md)。H1 的历史证据与八节设计见 [TOPOLOGY_HISTORY](TOPOLOGY_HISTORY.md)。
