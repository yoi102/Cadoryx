# B18：实际 History 来源和最终目标成员

2026-10-08：独立 SDK24 子阶段已验收。Cadoryx 当前仍是 990 来源/SDK22，尚未接入本接口；A–F 未完成，G 按用户要求暂缓。

## 已实现的原生关系

私有来源位于 `OcctSharp/artifacts/cadoryx-b18/source-gh-1/OcctSharp`，版本 `8.0.1-preview.31.cadoryx.curve-inspection.24`、ABI 1.83、桥 0.91.0。主 SDK 工作区以及已冻结 SDK22/23 来源保持不变。

`InputGraph` 在实际 copier 和运行副本存活时保留各参数的拓扑图。History 新增内部 `TopoDS_Shape` 来源值，原有公开 History 16 字节布局及枚举不变。副本源地址通过这个实际身份的原生成员查询形成；不由旧索引、相同坐标或数量推导。

`FinishDomains` 在 MakeSolid 和最终形状替换之后执行，逐个查询 History 目标是否确实属于实际最终结果。目标域和来源副本域地址分列。首末截面与独立 InputSnapshot 没有实际来源身份时保持缺失；快照不属于最终结果时明确记录域外。

公开 `AuthoringResult.HistoryDomains` 独立拥有数值数据，没有原生指针，来源/结果/计划释放后仍可读取。`InputDomainId` 连接同次实际结果的 `InputCopy.TargetDomainId`，GUID 本身不是身份依据。

| 数据 | 含义 |
| --- | --- |
| `CopiedSourceTopologyIndex` | 按实际原生源身份查到的参数副本域地址。 |
| `LegacySourceTopologyIndex` | 原 History 源地址，保留兼容；与实际副本地址不同单独报告。 |
| `SourceArgumentMemberships` | 实际源在不同参数图中的所属数量；多参数共享不声明唯一。 |
| `FinalTargetTopologyIndex` | History 目标在实际最终结果中的原生成员地址。 |
| `SourceIssues` / `TargetIssues` | 缺来源、无效参数、域外、类型不符、共享、无目标及无最终结果分别记录。 |

`HasUniqueNativeCopySourceMembership` 只证明实际来源属于一个参数副本域；`IsNativeMemberOfFinalResult` 只证明当次目标的最终成员关系。`ProvesCompleteOccurrencePaths` 和 `ProvesComposedReferenceIdentity` 保持 false。

## 实际验证

[完整独立审计](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-gh-1/history-domains-sdk24-verification-1.json)绑定 1016 来源、13 托管模块和实际加载的 62 原生库。

- 原生/托管 Release、真实 Debug 构建均 0 警告/0 错误；完整运行各 1210/1210、0 失败/0 跳过。逐名保留原 1166 项及新增 44 项。
- 新 44 项包含扫掠 16、偏移准备 16、兼容放样 8，以及布局、模拟无最终结果、共享来源拒绝和 C ABI 缺失/越界四项。
- 目标所属独立通过 `RepairSnapshot.FindTopologyIndex` 核对；原输入字节、1e-7 体积、1e-10 转换误差及释放后读取保持。
- 生成器两配置各 207 通过；4168 forwarders 由生成器产生并在两配置检查。14 私有包绑定已测载荷；全新本地缓存严格锁包消费者两配置各 42 公开历史域场景通过。消费者 Debug 使用包内 Release 原生，与真实 SDK Debug 分列。
- 首次许可说明重复 NU5118、首次消费者遗漏既有导入隔离而继承 CPM 导致 NU1008，均保留 FAIL。按原许可字节与原消费者隔离修复，未禁用警告、放宽锁包或修改建模断言。

SDK23 的 1014、SDK22 的 1011 和应用 990 来源逐哈希保持；本次没有发布包。无 OCCT 的另一仓库测试进程被保留，CAD/OCCT 原生消费者仍串行。

## 仍需完成

成员报告不证明内部共享出现路径，也不证明兼容拆分/合并的完整关系、最终材料组合、CopyInput 第三复制或保存往返。继续完成[同图原生保存桥](B18_NATIVE_SERIALIZED_REFERENCE_PLAN.md)、逐阶段组合、独立的新旧作者对应，再验证局部特征和工程图引用消费者。

缺少阶段、多路径、分裂/合并、陈旧资产/修订或未证明的 BRep 往返仍要求明确重选。当前 990 的完整 Debug、材质/通用窗口/正式 DLL 故障和原供应商工程图八轮继续按各自记录推进，不能由 SDK 子阶段替代。
