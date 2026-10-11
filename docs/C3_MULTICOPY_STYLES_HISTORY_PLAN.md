# C3 续批：多副本结果的逐面样式及原生拓扑历史

更新：2026-10-04。接着 [B12](B12_CIRCULAR_CONTACT_RELATIONS.md)验收，属于 [A–F 总跟踪](AF_IMPLEMENTATION_TRACKER.md)的 G10/G17/G21。本地契约最终已验：2296/2296、同冻结六窗口、最终交换及57截图/4矢量PDF审计完成，见实施记录和artifacts/c3-styles-verification-1.json。第七批 G 暂缓。本批一次覆盖模型、样式、历史、重算、存储、作者及交换联动，不能仅增加一个颜色显示入口便算完成。

当前证据：锁定 `.curve-inspection.8` 独立消费者五项通过，52 个真实 Compound 面/边/点成员和 52 个独立 repaired copy 反例、两组完整 Boolean 源历史已验，62 个包内/实际加载原生 DLL 哈希一致；明确记录 transform/compound 完整历史 API 缺失。绑定层新增 `ShapeHistoryModeling.Transform/Compound`，原生 Release 和托管构建、12 个任意帧/双反射/共享多来源/寿命/非法输入专项已过，facade forwarder 由工具生成。后续 Release 与实际 Debug 各701/701、Generator205/205、14不可变 .9 包与独立消费者9/9通过，13托管/62原生payload同哈希；41077出口只加2无删除。fresh generation169既有文件同字节、6个新增bulk/curve输出，manifest不相同，原因是恢复四个漏带Generator输入，不宣称.9完全干净重生。应用历史首个候选281联合通过；.10独立通道/隐藏状态/元数据前提另有713全量Release及实际Debug、12专项、205Generator与16独立消费者证据。十四不可变.10包的13托管/62原生payload绑定Runtime与消费者，41081出口加4无删除，94native单元/642手动出口、45头文件通过。消费者首版14/16失败来自Native重开的raw fingerprint变化，后版完整有序图及每槽canonical BRep复证通过；旧失败保留。应用后续191联合、17样式专项及1工程图/后继联动通过；容器完整原生成员证明及三语言生产控件已接入，最终2296全量/同候选六窗口及交换复验通过，见 [实施记录](C3_MULTICOPY_STYLES_HISTORY.md)，不能把SDK前提或单轮专项记作整个C3完成。见 `artifacts/c3-multicopy-sdk-1/package-audit.json` 和 OcctSharp `artifacts/cadoryx-c3/`。

## 已查明的起点

当前 `OcctRepetitionGeometry.Repeat` 对副本调用 `source.Transformed`，复合输出用 `CreateCompound`，融合输出逐次 `Fuse`；没有把变换/融合的原生完整子形状历史带入 Cadoryx。现有 `AttachLocalMirrorStyles` 只为规范局部 Z=0 的单个反射输出附加 XDE 上下文。

当前锁定 `.curve-inspection.8` 的 `XdeDocument.CopyTransformedDefinition` 可复制简单定义的逐子形状颜色/图层/材料；该契约明确使用 native transform history，拒绝 assemblies/scaling，并要求事务失败回滚。它不直接证明多个副本组合或 Boolean 融合后的全图对应。开始前须用独立锁定消费者证明这一缺口所需的完整变换及组合历史 API；若确实缺失，再补桥接和不可变本地包，不用位置、面面积、最近距离或原索引猜测。

## 本批完整范围

1. 线性、圆周和镜像的多副本 Compound：包含来源/排除来源、任意局部帧、持久 CopyIds 和完整单实体/多实体源均按显式作者布局处理。每个副本的原生变换来源单独记录，最终 Compound 成员须通过原生 membership/完整图复证，不能借独立 repaired copy 冒充原成员。
2. 融合输出：每一轮 owned Boolean 保留实际 operands 和完整原生 history，并把已证明的 transform→Boolean 路径组合。删除/分裂/合并及多个来源可能贡献同一个面，须保留全部真实来源及诊断，不把多对一改写为唯一对应。
3. XDE 样式：验证源定义真实 BRep、位置和标记子形状证据。各副本颜色/图层/材料从原生映射继承。融合后一个面有多个不同样式来源时明确报告冲突和整体样式策略；相同样式来源可以合并，不能悄悄丢色或把任意一个来源当唯一作者。不复制结果已失效的质心/PMI或未经证明的属性。
4. 历史/下游：完整源 Face/Edge/Vertex 映射进入已有 HistoryStep，原始引用存在多个副本时正确报告多解。若需要定位某副本，必须由作者明确 CopyId 和原生证明的路径共同指定；不能默认选择第一份。来源重算、数量/布局改变、抑制/恢复、独立化和融合拓扑变化保持冻结/明确重选规则，并验证局部特征、制造附着和工程图联动。
5. 参数/存储/作者：既有 C1 驱动和三语言生产重复编辑器/Ribbon/树使用同一契约，`cad_model` 与 inspect 返回完整样式策略/历史诊断。持久历史和 XDE 上下文租约有独立 schema/capability 与中间迁移，旧 schema 夹带新数据拒绝；不把缺失历史伪装成空的成功映射。
6. 交换与失败：STEP 导出后用独立包消费者和外部 CAD 实测副本几何、层级及实际逐面色；XDE 原生保存/重开也逐项核对。源资产指纹保持不变，导出隔离使用真实 BRep 重读，不能靠共享 TShape 的 Clone。超时、取消、失败事务及晚到预览回收所有资产，不覆盖旧失败证据。

## 验收安排

独立 SDK 先证明 native transform/Compound/Boolean 全图及 marked-face 样式，用不同有色面、任意帧、两次反射、重叠/相切/分離副本、共享/分裂/合并与额外拓扑正反例。然后完成应用专项和源驱动/参数/独立化/后继/图纸/存储/作者联合，整个方案 Release、全量回归和同一冻结三语言窗口。测试/窗口/消费者的共享应用、SDK 托管和全部 62 个原生 DLL 哈希分别绑定，截图直接查看，外部 CAD 和真实供应商/物理输入证据仍分别取证。

这是下一子批的实施契约，不能用此计划或单个 API 的存在宣称任务已经完成。其他 A–F 的性能、更多草图关系、螺纹、曲面终止/面编辑、制造规则、供应商失败继续保留。
