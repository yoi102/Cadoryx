# B18：引导建模输入复制域接续

2026-10-08 新接续：[SDK24历史域](B18_GUIDED_HISTORY_DOMAINS.md)已通过真实Release/真实Debug各1210、生成器各207、4168生成forwarders、14私有包和全新锁包消费者两配置各42场景。算法History来源按实际TopoDS身份查询运行副本域，目标在最终结果替换/MakeSolid之后核对；缺来源、共享及域外快照明确保留。SDK23/SDK22/应用990来源保持，应用未接SDK24。以下SDK23的“历史地址未实现”记录保留当时边界；当前成员报告仍不证明内部出现路径、兼容拆分/材料、第三复制、保存往返或跨作者对应。下一步按[同一ShapeSet保存桥](B18_NATIVE_SERIALIZED_REFERENCE_PLAN.md)推进，尚不允许自动重绑。

2026-10-08 最新验收：独立SDK23复制参数域子阶段已经实现并通过[完整独立审计](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-guided-copy-1/copy-domains-sdk23-verification-1.json)，下述“未编译”段落为先前准备记录。1014来源、ABI1.82/桥0.90.0；真实Release/真实Debug完整各1166/1166（原1151+新15）、生成器各207、4164生成forwarders两配置检查、14私有包和消费者Release/Debug全部通过。原生复制关系在copier存活时读取，目标地址由原生身份成员查找形成；共享参数成员与拒绝、独立计划/运行域、旧History布局/枚举、原字节和释放后报告读取已验证。SDK Debug使用真正Debug原生；消费者Debug使用包内Release原生，两种证据分列。

首次两类托管分析错误和打包资源遗漏已保留，修正未禁用规则或修改旧行为断言。第一消费者缓存导致targets文件路径261字符、MSBuild未导入，产生真实缺DLL失败；第二消费者以相同源码、相同14包和更短新缓存自动导入全部62内容项，未手工拷DLL，公开API/锁包/载荷及实际加载全部核对。源码主SDK及冻结SDK22保持原字节，应用仍990/SDK22，**SDK23应用接入未运行**，未发布任何包。

本次完成范围仅是两个实际复制边界的参数域关系。TopExp折叠方向及内部共享出现路径，故`ProvesCompleteOccurrencePaths`、`ProvesHistorySourceAddresses`、`ProvesFinalResultIdentity`保持false。算法来源地址、兼容/MakeSolid/最终材料、CopyInput第三复制、同一原生序列化引用解码和跨作者对应继续实现；一次复制或域GUID不能授权自动重绑。下一步先在已验SDK23来源上验证History实际来源/最终目标所属，再推进同一ShapeSet原生引用的保存桥；SDK及消费者通过后才接应用的独立证明数据和消费者。A5当前材质/通用窗口/正式DLL故障及其余A–F继续，G暂缓。

2026-10-08 当前990完整Release5756及A5两轮固定direct/生产Open已独立核对通过。独立SDK草稿的[两配置构建流程](../artifacts/run-b18-guided-copy-build-1.ps1)已经串行启动，当前处于真实Release原生编译；尚无完整构建终态，15用例/旧SDK回归/干净消费者未运行，新forwarders尚未生成，应用仍使用SDK22。运行中的编译不能计作已通过或已接入。

2026-10-08 当前接续：已从冻结 SDK22 的 1011 个来源/夹具文件建立独立副本，并准备 1014 文件的复制报告草稿与 15 个新用例，见[草稿冻结回执](../../OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-guided-copy-1/copy-report-draft-freeze-1.json)。草稿在实际 copier 存活时查询完整来源域的 ModifiedShape，再以原生成员查找定位独立目标域；共享参数成员、缺失、域外、类型不符和原身份复用分别记录。调用者到计划及计划到运行的域标签分别保存。旧 History 16 字节布局与枚举源码相同，原 SDK 测试只调整新 ABI/桥版本身份断言，其余保持。SDK 主工作区及冻结 SDK22 未被本次工作修改，应用仍是当前990。

这是**未编译、未运行、未接入应用**的草稿，候选 ABI1.82/桥0.90.0/包23不是已交付版本。TopExp 会折叠方向及内部共享出现路径，所以参数成员数量不代表完整路径证明；算法 History 来源地址、兼容处理/MakeSolid、第三次 CopyInput、最终材料组合及保存往返仍未证明，相关 proof 标记保持 false。准备脚本的一处变量名定位错误及用例计数纠正已保留在回执中。先完成当前990完整 Release5756 的终态审计和 A5 固定条件验证，再串行编译 SDK、生成 forwarders、运行真实 Release/Debug 原生测试及干净锁包消费者；草稿15不计通过数。

2026-10-08只读核对：SDK22隔离来源实际路径为`C:/Users/yoiri/source/repos/OcctSharp/OcctSharp/artifacts/cadoryx-b18/source-curved-preparation-1/OcctSharp`，见[当前SDK冻结](../artifacts/b18-curved-sdk22-verification-1.json)。本页是接续方案，未修改SDK、编译或运行新复制证明；已有16个准备诊断和64条Modified所属证据不代表这些桥已存在。

当前代码的两个复制边界必须分别证明。`GuidedSweepPlan.Create`经`AuthoringBridge.CopyInputs`创建独立计划输入，原生`GuidedInputs.cpp`只返回每个复制根的InputSnapshot，没有子形状来源地址。运行时`GuidedAuthoring.hxx::InputGraph`又执行一次整体BRepBuilderAPI_Copy；当前仅对输入根调用ModifiedShape，算法History的子形状索引来自第二次复制后的域。计划的CopyInput还会产生第三个独立副本。不能把这些域中的同一整数位置当成身份相同。

下一批按依赖一次推进四组实质工作：

1. **调用者到计划输入。** 在真正的Copy操作仍存活时，对原始每个输入的完整拓扑域查询ModifiedShape，再在相应复制根的域上用原生身份查询成员。原源槽位/类型、复制目标域及其真实成员位置分别保存；缺失、多目标、不同类型、共享来源的多路径明确报告，不根据数量或位置补映射。保留源Free标志和字节不变。
2. **计划输入到运行输入。** 将实际第二次复制对应保留在本次运行的独立域数据中，再组合算法Generated/Modified。每条旧History.Source必须说明属于哪个域；旧接口/数值/布局保持，新证明独立版本化，不把计划GUID变成未验证的拓扑身份。
3. **最终实体与实际序列化地址。** 对MakeSolid、兼容处理、BooleanCut/Compound每一步验证目标属于真实最终结果。保存桥优先研究同一次原生ShapeSet写入中的直接成员查找及原生读取地址；该机制尚未验证，不能由TopExp遍历索引、相同数量或BRep哈希单独推定。若不能证明，保存后继续要求重选。
4. **应用消费者与失败矩阵。** SDK真实Release/Debug、生成器及干净锁包消费者分别通过后才接入应用，补当前结果明确选择、局部特征、工程图附着、存储及取消/迟发边界。跨作者重算还需要旧结果到新结果的独立对应；一次复制证明不能授权自动延续。

先覆盖现有64个曲面/方向/姿态/原始-C1场景，再覆盖重复/共享输入、释放原源、错计划/参数槽位、原生无映射/多目标、最终实体不含目标、遗漏桥、篡改数据和BRep回读。每个映射只能由真实原生关系和成员查找形成；独立体积、作者字节、现有1e-7/1e-5公差及草图/装配预算保持。计划中的场景不计为通过数。

该工作不会改写当前SDK22或把复制诊断计为B18完成。A5当前性能/材质、B间接表达式/混合接头、C新配方参数、D扩展终止、E直接编辑及F制造/外部往返/模型会话继续见[A–F跟踪](AF_IMPLEMENTATION_TRACKER.md)；G暂缓。完整约束仍见[B18原生历史组合](B18_GUIDED_HISTORY_PROVENANCE_PLAN.md)。

## 保存桥的进一步源码核对（尚未实现）

已核对当前OCCT8.0.1头文件：`BRepTools_ShapeSet`维护同一形状集合中的拓扑、位置与几何；继承的`Write(shape, stream)`明确写原生TShape与Location引用，`Read(shape, stream)`读取同一集合中的形状引用。这提供了一个需要实测的候选：在实际写入集合上写出根和选中成员的原生引用，再由同一次解码集合读取这些引用，并对解码根验证原生成员关系。不能直接把写入表Index、TopExp索引或回读遍历位置相等当成身份证明。

这一候选尚未编译或运行，也尚未证明与现行BRep资产编码的兼容性。必须核对同一实际字节中的根/成员引用、方向及完整Location、旧编码、错字节/篡改引用、共享出现路径和释放后读取；应用需要通过同一个原生解码图取得根及成员，独立两次解码后不能借相同字节哈希推定TShape相同。不能通过当前复制报告授权保存后引用接续；最终材料和跨作者新旧结果的证明仍独立待办。
