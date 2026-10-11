# B18：SDK26 完整出现路径接口草稿

2026-10-09，SDK26当前解码出现路径子阶段已[独立验收](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/occurrences-sdk26-verification-1.json)：1023来源、真实Release/Debug各1316、生成器各207、4176转发器、14包及干净公开消费者两配置原42历史+28保存+新32路径场景通过。新公开矩阵每配置3208路径、2856歧义行；实际SDK Debug原生和消费者包内Release原生分别绑定。当前应用993保持SDK25，尚未接入SDK26。完整历史/材料/第三复制/跨作者仍未证明，正式A4/B17/C5/D5/E4/F5g、A–F ACTIVE、G暂缓。

2026-10-09 当前1023来源/实际两配置0警告0错误、4176转发器、原1257加新59共1316两配置全部通过；新增59两配置也独立核对。生成器各207和14私有包内容审计通过。全新缓存公开消费者正在执行原42历史/28保存与新32路径矩阵，无终态。应用仍993/SDK25，未接SDK26；完整组合身份与其余A–F继续。下面初始草稿段落保留历史状态。

2026-10-08，已从冻结 SDK25 的 1020 个来源文件建立独立副本。当前草稿为 1023 来源，候选包 `8.0.1-preview.31.cadoryx.curve-inspection.26`、ABI 1.85、桥 0.93.0；真实 Release 原生构建已启动，无终态。59 个新用例已准备，预期原1257加新59共1316，发现清单和实际运行尚未验收。canonical SDK、SDK22/23/24/25 和应用993保持。

## 已实现的草稿

`BrepOccurrenceSnapshot.Capture` 在实际源图存活时完整遍历 `TopoDS_Iterator`，保留每个父子路径以及累计方向和位置。每行捕获同一 `BRepTools_ShapeSet` 的实际原生引用，随后独立解码并核对每条路径和对应原生引用 `IsEqual`。路径集合必须覆盖真实遍历的全部节点，重复、缺失和越界路径拒绝。

新格式为独立的 `OCROCC26/schema1`；原 `OCRREF25/schema1` 和既有引用接口保持。完整路径限制 512 行、64 层，每份 BRep/表限 64 MiB。超预算拒绝完整证明，旧建模与引用接口仍能处理原有模型，不把证明预算变成建模规模限制。

`Open` 只证明当前解码图的完整路径，保存前来源始终未知。摘要是完整性绑定；即使修改声明来源地址并重新计算摘要，也不能取得 `RestoreFromProvenNativeSource` 的权限。GUID 只标识报告域，不授权身份延续。

`BrepOccurrenceRestoreResult` 持有原生查询所有者、根和各成员。`Resolve(path)` 对已经原生验证的明确路径返回独立拥有的成员句柄；该成员可以在结果关闭后独立存活。`FindSameIdentityOccurrences` 查询实际 `IsSame`，保留所有出现路径；`FindExactIdentityOccurrences` 另查实际 `IsEqual`；`ResolveUnique` 要求恰好一个同身份路径并有精确方向匹配，多路径、域外成员或方向不符时拒绝，不按索引或几何猜选。

取消检查位于调用前、原生调用完成后和托管成员提取期间。它不声称可以中断正在运行的 OCCT 解析；完整恶意解析资源隔离仍未证明。

## 验收队列

| 门禁 | 当前状态 | 要求 |
| --- | --- | --- |
| 来源冻结 | 当前1023已核对，旧1257逐名保留 | 1023 来源；冻结 SDK25 的1020来源保持，旧测试只修改精确ABI/桥版本断言 |
| 真实两配置构建 | 两配置0警告/错误，4176转发器检查通过 | 两配置使用对应 OCCT 原生，零警告/错误；源驱动生成 facade 转发器 |
| 新用例 | 两配置各59/59，0失败/跳过 | 8种图×位置×正反向32；10专项；17篡改负例；保留原1e-7公差 |
| 完整原生回归 | 真实Release/Debug各1316/1316，实际62分别绑定 | 原1257用例及新59全部实际执行，来源/程序集/实际62绑定 |
| 生成器 | 两配置各207/207通过 | 两配置执行原生成器清单，不以生成成功替代编译/运行 |
| 14包与干净公开消费者 | 14包及干净锁包消费者两配置42历史/28旧保存/32新路径全部通过 | 旧历史/保存消费者和新路径消费者分列，两配置严格锁包/实际62 |
| 应用接入 | 未运行 | 当前SDK25应用保持；接口验收后接独立证明分区和当前结果明确选择/保存重开 |

篡改负例包括错误标记/版本/行数/路径总数/深度、根类型/方向、拓扑预算、行类型/方向、引用方向、错误/重复路径、共享数量、校验和、截断及其他图的 BRep。还覆盖独立复制/解码身份拒绝、预取消、释放后值可读、原生短缓冲区以及新旧格式互相拒绝。

SDK26 没有关闭准备、兼容、MakeSolid、材料、第三复制及跨作者 C1 的完整历史组合。通过本阶段不能自动提升 B18 或 A–F 的完成状态；正式 A4/B17/C5/D5/E4/F5g 保持，G 暂缓。

## 证据入口

- [SDK26完整独立验收](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/occurrences-sdk26-verification-1.json)
- [公开消费者](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/consumer-1-verification.json)
- [已验原生机制](B18_OCCURRENCE_REFERENCE_MECHANISM.md)
- [原1020来源副本](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/preparation-1.json)
- [接口草稿冻结](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/occurrence-draft-freeze-1.json)
- [构建准备](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/build-runner-preparation-1.json)
- [当前构建脚本](../artifacts/run-b18-occurrence-sdk26-build-3.ps1)
- [当前两配置构建](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/occurrence-build-3-exit.json)
- [真实Release完整1316](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/runtime-release-full1316-1-verification.json)
- [真实Debug完整1316](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/runtime-debug-full1316-1-verification.json)
- [14包内容审计](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/package-serialized26-2-verification.json)
- [首次xUnit构建FAIL](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/occurrence-build-1-exit.json)
- [首次Facade资源打包FAIL](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/package-serialized26-1-exit.json)
- [托管接口](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/OcctSharp/src/OcctSharp.Modeling/Serialization/BrepOccurrenceSnapshot.cs)
- [原生接口](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-occurrence-1/OcctSharp/src/OcctSharp.Native/src/Modeling/SerializedReferences.cpp)
