# B18：应用同图引用资产与保存重开

2026-10-08，应用已从 SDK22 接到独立验收的 SDK25。当前为 **993 来源、5802 个发现用例**；原 5756 及原测试源码保留，新 46 项。Release/Debug 构建均零警告、零错误，21 份锁文件、包内 13 托管库与 62 原生库核对见[构建与清单审计](../artifacts/b18-app-sdk25-build-2-verification.json)。应用 Debug 使用包内 Release 原生库；SDK 真实 Debug1257 的证据仍在[独立 SDK25 验收](B18_SERIALIZED_REFERENCE_SDK25_DRAFT.md)。

首保存候选新增专项：[Release46](../artifacts/b18-app-sdk25-release-new46-2-verification.json)、[Debug46](../artifacts/b18-app-sdk25-debug-new46-1-verification.json)均通过，零失败、零跳过，实际加载 62 库与已验 SDK25 包逐哈希绑定。首相关657为656通过/1失败：旧预览明确要求1资产，新保存表导致2资产。原断言保持；已拆分文档保留与临时预览路径，修后[当前Release670](../artifacts/b18-app-sdk25-release-related670-2-verification.json)和[当前Debug670](../artifacts/b18-app-sdk25-debug-related670-1-verification.json)全部通过，零失败/跳过；[应用接入阶段审计](../artifacts/b18-app-sdk25-app-integration-1-verification.json)已完成。当前[Release完整5802/5802](../artifacts/b18-app-sdk25-release-full5802-1-verification.json)已经独立审计通过，零失败/跳过，50.2656分钟；[完整Debug5802](../artifacts/b18-app-sdk25-debug-full5802-1-verification.json)也已独立通过，零失败/跳过。当前[性能](../artifacts/a5-current993-performance-verification-1.json)、[材质组件21/76](../artifacts/a5-current993-material-verification-1.json)及[晚期导入DLL两故障](../artifacts/a5-current993-late-import-fault-verification-2.json)通过；通用前两轮FAIL、第三夹具待空闲，圆盘专项及生产引用窗口仍待验。990/SDK22 的旧完整回归与圆盘像素验收不能代替当前版本。

## 已接入的生产路径

文档新增、更新与重算使用 `EvaluateForDocumentAsync`；普通 `EvaluateAsync` 的临时预览保持原单BRep资产契约。`EvaluateGuidedWithReport` 在实际扫掠/放样结果仍存活时，从原生邻接快照借用 edge/face 成员，调用 SDK25 `Capture`。不使用 `RepairSnapshot.Create` 的独立复制成员冒充原结果成员。实际 BRep 与引用表分别进入内容寻址资产库。

`GeometryAssetRef.SerializedReferences` 保存表资产 ID、schema1 及 writer format；geometry 分区升到 MessagePack v2。该分区不保存 `NativeSourceCaptureProven`、历史证明或自动重绑标记，和 `guided-modeling-reports` 的建模观测分离。

`GeometryAssetLease` 同时持有 BRep 和表，`ReferencedAssets`、文档租约、资产目录、保存与加载都保留两者。内部结果转交和独立工程图/螺纹工作器会携带表；原 256 drawing instances、3 thread primary assets、总传递字节与截止时间保持，补充表集合按其实际来源验证。

`ReadReferencedShape` 使用 SDK 的 Open 校验后 Restore；最终 root 与 members 来自同一个 Restore 图。Open 本身也执行一次原生验证解码，不能把这条路径描述成总共只有一次解析。`ReadShape` 转交该图的 root，并释放单独成员句柄。`FindDecodedReferenceIndex` 对当前 root/member 实际查询原生身份，只校验当前解码地址，不从声明来源索引相等推导保存前身份。

## 旧文件、预算与失败语义

geometry v1 升级为 v2 时保持 BRep/ID/修订和缺引用表的状态；旧分区夹带新引用字段会拒绝。带引用表的文件必须声明 `cadoryx.native-reference-tables.1`。表媒介、版本、数量/令牌/字节维度与 BRep/table 绑定在托管侧核验；实际成员、类型/方向和规范原生令牌由 SDK 解码检查。哈希只承担完整性。

自动完整 edge/face 表限制为 512 行、64 MiB。大拓扑继续使用原建模契约保存，并给出 `MODELING.REFERENCE_LIMIT`；不发布部分表作为完整证明，不把证据预算变成新的建模规模限制。140 边 loft 的完整原生实体和解析体积误差 1e-7 验证通过。

表 Stage 失败或 Stage 后取消会回收两份资产；会话/加载失败不发布部分文档。保存时维持原原子写入机制。旧文件无表时 `ReadReferencedShape` 明确拒绝要求当前图重新选择；普通几何读取仍兼容。

## 实际验证范围

- 8 种曲线消费者 × 正反向 × 倾斜与否，共 32 场景：生产扫掠/放样保存后关闭原会话，新的磁盘资产库分别普通/延迟重开，保留原 1e-7 体积、公差、单实体、有效性和干涉检查。
- 当前最终图逐成员查询、普通 BRep 与另一次 Restore 的身份拒绝；来源形状/资产释放后，当前图仍独立拥有成员。
- 在实际借用成员 Capture 时存在真实来源证明；文件 Open/Restore 的原来源始终未知。重算 BRep/table 摘要并篡改声明来源地址，仍不能调用 `RestoreFromProvenNativeSource`。
- 缺表、表 schema、旧分区冒充、缺 capability、错误媒介/资产角色均拒绝且资产归零；旧文件迁移不生成证明。
- Stage 故障/取消，超预算大 loft，以及两种真实隔离工程图工作器投影与直接原生结果完整一致。

首[657预览资产数FAIL](../artifacts/b18-app-sdk25-release-related-1-failure-verification.json)以及 `.sln` 路径错误、新测试遗漏磁盘缓存目录的 CS7036，以及独立审计首次把已变化的测试锁文件也当作原测试源码的错误均保留。修正路径/夹具/审计分类；原测试源码、几何断言、公差、期限没有改动。

## 仍须完成

这不是整项 B18 或 A–F 完成。下一阶段按依赖继续：

| 顺序 | 要关闭的边界 |
| --- | --- |
| 1 | 相关670及完整5802两配置已通过；当前性能/材质组件/晚期导入DLL两故障已验。通用/圆盘和生产引用窗口待验，应用材质创作/完整进程内渲染/GPU故障仍分列。 |
| 2 | 把当前解码成员与局部特征、工程图显式选择的证明契约组合；独立 Repair 复制仍要有真实原生对应，不能用相等索引或指纹替代。 |
| 3 | 接入已验 SDK23/24 实际复制参数域、来源/最终目标成员报告，逐阶段组合准备、兼容、MakeSolid、材料和第三复制。 |
| 4 | 独立[出现路径机制](B18_OCCURRENCE_REFERENCE_MECHANISM.md)两配置各955路径及[SDK26完整当前解码路径](B18_OCCURRENCE_REFERENCE_SDK26_DRAFT.md)已验；应用仍993/SDK25。按[独立分区接入决定](B18_APP_OCCURRENCE_INTEGRATION_PLAN.md)继续保存/租约/工作器和显式消费者，不把独立Capture的两个BRep按哈希强行绑定。保存前来源持续性以及跨作者 C1 对应仍需要独立证据；缺失、共享、多路径、split/merge 保守拒绝并要求重选。 |
| 5 | 原供应商 `prism-ml/bonsai-27b` 工程图第三轮 FAIL 和其余 A–F 门禁继续，不改原模型/提示/16轮预算。 |

完整恶意 OCCT 解析的进程资源隔离未证明。正式里程碑保持 **A4/B17/C5/D5/E4/F5g**；A–F ACTIVE，G USER_DEFERRED。
