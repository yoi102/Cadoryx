# B18：原生出现路径与同图引用机制

2026-10-08，独立原生探针在真实 Release、Debug 两配置各完成 8 种图、955 个出现路径，分别有 848 行存在共享身份的多路径。两配置报告完全一致，实际加载的 16 个 OCCT 模块分别匹配冻结 SDK25 的对应原生配置。这是机制证据；[SDK26接口](B18_OCCURRENCE_REFERENCE_SDK26_DRAFT.md)已完成独立子阶段验收，尚未接应用，应用集成及完整历史组合未完成，正式批次仍为 A4/B17/C5/D5/E4/F5g，G 暂缓。

## 为什么不能只使用扁平拓扑表

`TopExp::MapShapes` 合并相同 TShape/位置的成员，并不保留每条父子出现路径。普通 Box 的扁平成员为 34 个，实际包含 86 个出现路径；同一个边可以通过不同面到达。单凭表索引、计数、几何或指纹，不能确定用户所指的出现路径。

探针递归遍历实际 `TopoDS_Iterator`，保留每层父子路径及真实累计 Orientation/Location。源图仍存活时，对每条路径查询实际原生成员，并在同一个 `BRepTools_ShapeSet` 中生成该成员的原生引用。随后销毁全部源根、成员句柄、复制器和编码器，再解码根和引用。

对解码图中的每条路径，必须验证遍历到的实际成员与原生引用读取的成员 `IsEqual`，包括方向和位置；独立解码的相同 BRep 必须拒绝身份续接。路径编号只定位实际遍历节点，不能独立授权重绑。相同原生身份的多路径全部保留为歧义。

## 实际结果

| 图 | 扁平成员 | 出现路径 | 多路径行 |
| --- | ---: | ---: | ---: |
| Box | 34 | 86 | 72 |
| 反向 Box | 34 | 86 | 72 |
| 倾斜、平移 Box | 34 | 86 | 72 |
| 共享正反向与嵌套位置 Compound | 104 | 346 | 316 |
| Bézier wire | 4 | 4 | 0 |
| 实体 pipe | 34 | 86 | 72 |
| 兼容 loft | 34 | 86 | 72 |
| 同一 Box 经两个不同父节点出现 | 37 | 175 | 172 |
| 合计 | 315 | 955 | 848 |

两个配置还实际检查了独立复制/独立解码拒绝、原生类型与方向、实际共享路径数量、越界路径拒绝、行数/深度预算拒绝、普通 BRep 读取兼容以及原 1e-7 体积条件。未把相等路径或索引作为身份断言。

## 下一阶段的接口边界

已建立[私有来源副本](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/preparation-1.json)，1020 个来源文件逐字节等于已验 SDK25；首次复制时仅形成同源副本。随后已实现[SDK26接口草稿并启动真实构建](B18_OCCURRENCE_REFERENCE_SDK26_DRAFT.md)，后续真实两配置1316、生成器207、14包及公开42/28/32消费者已独立验收，应用接入仍未运行。canonical SDK、冻结 SDK22/23/24/25 与应用993保持；下列接口/消费者门禁仍须逐项完成。

1. 从已验 SDK25 建立私有来源副本后增加有界、独立拥有的出现路径快照。保留旧引用接口、schema1、512 行和 64 MiB 契约；完整证明超预算时明确拒绝，不发布部分记录作为完整图，也不限制原有建模规模。
2. 保存每条路径的实际原生引用、方向/位置和歧义信息。新格式单独版本化；文件 Open 不追认保存前来源，修改路径或重新计算摘要不能补出来源证明。
3. 为错误路径、不同图的引用、反向/位置不符、超预算、截断、取消和释放建立正负消费者检查；实际 Release、真实 Debug、生成器及干净锁包消费者分列验收。
4. 接入应用后，先验证当前结果显式选择的保存重开。准备、兼容、MakeSolid、材料、第三复制及跨作者 C1 的每个边界继续需要独立原生对应；多路径、split/merge 或任何缺失仍要求重选。

当前探针没有生产资产格式的篡改守卫、恶意 OCCT 解析完整资源隔离或跨作者证明。原生遍历中的预算检查不能替代上述验收。

## 证据

- [独立核对终态](../artifacts/b18-occurrence-reference-probe-1/probe-verification-1.json)
- [首次运行前冻结](../artifacts/b18-occurrence-reference-probe-1/source-freeze-1.json)
- [两配置运行](../artifacts/b18-occurrence-reference-probe-1/probe-1-exit.json)
- [探针源码](../artifacts/b18-occurrence-reference-probe-1/main.cpp)
- [运行脚本](../artifacts/run-b18-occurrence-reference-probe-1.ps1)
- [独立核对脚本](../artifacts/verify-b18-occurrence-reference-probe-1.py)
- [应用保存接入与剩余范围](B18_APP_SERIALIZED_REFERENCES.md)
