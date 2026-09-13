# H2-B1：多输入历史协议与接入准备

H2-B2 更新：生产布尔逐源接口已通过本地 NuGet 接入，见 [真实布尔历史](BOOLEAN_HISTORY.md)。以下保留 H2-B1 的协议设计及当时尚缺上游接口的阶段证据。

2026-09-13。此批完成 Cadoryx 侧的多输入历史结构、歧义归约、命令提交和 MessagePack 演进。生产 OcctSharp 仍为 preview.26；没有用合成关系替代真实布尔历史。布尔逐源内核接口及多步传播仍是 H2-B 的后续门禁。

## 领域与输入身份

`TopologyHistoryEntry.SourceArgument` 指向算法输入参数，下标从 0 开始；`SourceIndex` 仍指向该参数内部的 full topology 槽位。两者构成来源键，因此输入 0 的面 5 与输入 1 的面 5 不会混淆。关系中的结果索引指向同一个精确输出图。

`TopologyHistory` 保留 H1 的首源字段为参数 0，新增 `AdditionalSources` 表示参数 1..N，按配方输入顺序排列。每个 `TopologyHistorySource` 有精确 GeometryRevisionId、AssetId、Fingerprint 和 TopologyCount；`GetSource(argument)` 提供统一访问，最多 256 个输入。重复几何资产可以出现在不同参数位置，但来源参数身份仍不同。

地图协议 SchemaVersion 1 只允许参数 0；SchemaVersion 2 允许多输入，并要求每个输入都有关系记录。校验索引时使用各自输入的 TopologyCount，而不是首输入数量。源类型一致性按 `(SourceArgument, SourceIndex)` 分组，结果类型一致性仍按 ResultIndex 分组。真实解析还必须验证源图中每个应跟踪子形状均有证据，不能将这个基础结构校验当作完整 native 证明。

`ValidateFor` 对 LocalFeatureRecipe／BooleanRecipe 检查证据输入数、依赖数、配方输入顺序、双端修订与资产。DocumentSnapshot 进一步核对每个输入槽位的上游 Feature.Result，避免同时篡改配方与历史却重排特征依赖。历史不新增额外资产，所有来源必须对应配方和上游已有资产。

## 归约和事务

`TopologyHistoryReduction.Resolve(history, sourceArgument, sourceIndex, kind)` 返回指定输入的关系。旧三参数重载继续表示输入 0。越界参数或源槽位返回 WrongContext。

同源一对多仍为 Ambiguous；只要另一个来源键指向同一结果，即使它拥有相同的 SourceIndex，也判为合并歧义。Deleted、Generated、Unmapped 的保守规则保持不变，不挑第一个结果。

BooleanCommand 现在与局部建模一致，将 GeometryResult.TopologyHistory 与精确几何原子加入候选。FeatureRecompute 原有整体替换路径适用于多输入；某个上游输入变动后必须提供全新的关系表。新结果没有证据时明确清空旧证据。失败不提交，精确撤销恢复完整旧快照，不能混用旧映射和新几何。

## history v2 文件节

应用写出版本 0.4.4；仍为八节 MessagePack。history 节升级为 2，其余节版本不变；新增必需能力 `cadoryx.topology-history.2`。地图自身的 SchemaVersion 与文件节版本独立，旧单输入证据保留 SchemaVersion 1 和原 AdapterVersion。

在已有稳定数字 Key 后追加字段：

| DTO | 新字段 |
|---|---|
| PackHistory | Key 12：AdditionalSources，v2 必须明确为数组，可为空 |
| PackHistorySource | Key 0..3：Revision、Asset、Fingerprint、TopologyCount |
| PackHistoryEntry | Key 5：SourceArgument |

history v1 → v2 迁移为旧证据添加空 AdditionalSources，既有项的 SourceArgument 为 0，不运行几何算法。拒绝把多输入地图或非零参数关系伪装在 v1 节中。document v4 引入历史的旧迁移输出冻结为 history v1，再显式迁移到 v2，避免随 CurrentFormats 跳版本。

冻结 `m4t1h2a-history.cadoryx`，来源 `artifacts/smoke-20260913-104928/history-Fillet.cadoryx`，SHA-256 为 `cc4722b54c3a96dc9fbd65f9bd470c24ab2fd785d66e89f89dc856240da17528`。测试验证迁移保留轴校验版本、原修订／状态／资产和每条关系，并在真实 BRep 上重新解析。此前九份文件保留原字节。

## 核心能力边界

MultiInputHistoryTests 的合并、输入重排和多输入保存使用明确标记的合成证据，几何本身仍由真实内核生成。这些测试证明协议和生命周期，不能证明实际 Boolean 原生关系。普通 OcctGeometryKernel 对 BooleanRecipe 仍不给出历史；即使文件携带测试适配器的完整地图，生产 TraceAsync 仍返回 Unsupported。

本轮额外检查了 OcctSharp 的 PartitionResult：它提供 InputIndex 和源 TopologyIndex，但 CopyHistoryShape／CopyOutput 均由 RegionStorage 独立深复制；RegionHistoryReference 没有最终输出的 full topology index。因此仍不能把几何副本拼成可信的输出原图定位器。preview.27／28 在本地包目录中存在，但这不等于其具备所需接口，也没有因此修改当前消费版本。

下一步应在 OcctSharp 完成同次 Boolean 构建中的逐源索引与最终图索引契约、真实拆分／合并／删除／未改变与所有权测试，生成独立本地测试 NuGet 后再接入。接口最低要求及既有能力证据见 [HISTORY_ROTATION](HISTORY_ROTATION.md)。本批不开放多步用户引用、自动重绑定或连续局部建模。

## 本轮验证

新增 16 项协议测试，全套 298 项通过、0 失败（`artifacts/h2b-all-final.log`）；全解决方案 Release `--no-restore` 构建 0 警告／错误（`artifacts/h2b-release.log`）。其中冻结 H2-A 测试用真实 BRep 解析验证旧单输入历史迁移；多输入事务和合并歧义仍使用明确的合成关系。10 份固定存储样本 SHA-256 均符合记录，121 个本地 Markdown 文件链接及 `git diff --check` 通过。

完整 `-PublishSmoke -WindowSmoke -RecoverySmoke` 验证在锁定还原阶段被用户级 NuGet.Config 的沙箱访问限制阻断，提升权限申请又因自动审批服务额度耗尽被拒绝。本轮未完成锁定还原、发布和桌面门禁，H2-A 的旧结果不作为本轮通过证据。阶段状态及后续顺序见 [ROADMAP](ROADMAP.md)。
