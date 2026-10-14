# F4 交换实测与外部往返记录

2026-10-14，status275：新报告为schema2，增加实际实例共享分区、世界平移和方向差异；schema1仍按原比较行验证。[独立证据](../capability-evidence/diagnostics/f4-occurrence-semantics/verification.json)记录两配置10项报告测试及第八候选原真实交换测量39项，全部通过；报告测试的ZIP载荷是明确的hash-only文本，不作为CAD几何证明。两配置扩大专项各291通过；完整7184和外部CAD完整语义验收仍待终态，不以旧FreeCAD结果替代新范围。

2026-10-08 **status104 当前972/SDK22/solver17真实曲面交换139及相关1113，Release/Debug各1252个不重复用例通过、零失败/跳过，实际62原生及完整载荷逐项绑定。每配置保留128生产保存档案/128实测STEP或IGES包；包围盒改用六个原生外侧平面距离见证，保留原1e-5阈值，最大回读差3.0554e-6mm；源模型独立体积最大差7.6517e-8，原1e-7保持；STEP体积最大回读差2.4152e-11，IGES面表示仍NotComparable。STEP声明改为生成BRep，明确建模近似、无逐结果证明和作者语义损失；精确棱柱分支不误报。原128夹具失败、20包围盒失败及4距离守卫失败均保留。两配置构建零警告/错误，5381清单保留原5242；当前完整5381/完整Debug尚无通过终态。此前967的24窗口/111图和圆盘7次分列；当前972通用窗口/材质/性能及外部曲面CAD未验。逐结果精度/保守原生历史、间接表达式、混合接头和其余A–F继续；正式已验仍A4/B17/C5/D5/E4/F5g，G暂缓。** 见[当前139两配置](../artifacts/b18-curved-exchange-verification-2.json)、[当前相关1113两配置](../artifacts/b18-curved-exchange-related-verification-1.json)、[交换测量说明](B18_CURVED_EXCHANGE.md)及[下一批历史证明规划](B18_GUIDED_HISTORY_PROVENANCE_PLAN.md)。

2026-10-03：实测交换的当前有界契约已实现并验证。界面和 CLI 可以导出 STEP、IGES、STL，实际重读后产生独立报告；另有真实 FreeCAD 往返证据。整个 A–F 请求仍在进行中，第七批实机、安装及版本交付暂缓。

## 实测报告

工程交付窗口增加“交换往返实测”选项，三种语言均已接入实际控件。它现在生成 schema2 ZIP，内容为 `report.json` 和选中的真实交换文件，不附带原生文档或 BOM；旧schema1包仍按原比较契约校验。已有交付包及能力声明继续保留，原能力报告中已过时的“源逐面样式不导出”声明已纠正。

报告绑定源 `DocumentId`、`StateId`、内核版本、资产哈希、选项以及各输出文件的字节数和 SHA-256。观测包括世界坐标边界、按实例累计的面积、已知单实体体积、体/面/边计数、定义复用计数、有序层级及名称、有效颜色集合。比较结果可以为不同或不可比较；完成报告生成不代表模型完全等价。

schema2另外比较按首出现顺序规范化的定义共享分区（含装配定义）、各实例世界平移分量（1e-5mm）和旋转后的三个单位轴分量（1e-7，无量纲）；四元数q与-q视为相同方向。相同复用总数、整体包围盒或质量不再掩盖共享分区或单实例位姿变化。只有有序图的名称、定义名、深度和种类相同才进行上述比较，否则三项均为NotComparable，不推断节点或持久拓扑对应。版本号不能用来伪装增加或删除比较行；公共四参数Compare签名保留。

只读快照在工作线程中导出和测量，全程保留资产租约。失败、取消、过期结果不改变文档或覆盖旧输出；同目录临时文件完成并校验后才原子替换。CLI 示例：

```powershell
Cadoryx.Cli.exe exchange-audit model.cadoryx --output measured.zip --iges --stl --memory
Cadoryx.Cli.exe verify-exchange measured.zip
```

默认包含 STEP；可按既有参数添加 IGES、STL，或使用 `--no-step`。`verify-exchange` 检查条目白名单、大小、哈希、报告身份和内部比较一致性；它不会独立重测几何，也不认证报告的外部来源。报告明确区分 Cadoryx/OCCT 自重读与外部 CAD 证据。

## 本轮修复

实测暴露了两处实际问题。非源样式 BRep 的根位姿被 XCAF 包装成引用后再次用于装配，会增加原点副本；应用现在只在临时文档中检查精确根位姿，并将它烘焙到输出定义，临时引用不进入输出文档。12 个 STEP/IGES 位姿回归覆盖这个问题。

独立 FreeCAD 首轮检查发现一个制造样本的 IGES 从 18 面、1436 mm² 变成 10 面、1124 mm²。原因是 IGESCAF 丢弃了以无界直线为基曲线的有限拉伸侧面。上游写出器现在复制 XDE 文档，只将精确判定为直线基曲线的拉伸曲面转成有限 B 样条，使用唯一原生修改对应延续副本的子形体元数据，并检查有效性、面数及面积。作者几何和应用拓扑身份不变。

中间 `.7` 候选转换范围过宽，应用全量出现四个圆形/圆弧周期面失败，已保留原日志。最终 `.8` 保留圆弧原始周期表示；同一批用例和完整回归都通过。修复只作用于 XDE/IGESCAF 写出路径，不改变原 `ShapeExchange.WriteIges` 路径。

## 最终证据

冻结包为 `8.0.1-preview.31.cadoryx.curve-inspection.8`，十四个本地包保持不可变。验证结果：

| 检查 | 结果 |
|---|---|
| Release 构建 | 0 警告、0 错误 |
| 交换及圆弧联合专项 | 87/87 |
| 应用完整回归 | 1722/1722，11 分 16 秒 |
| 新增原生专项 | 12/12 |
| 原生完整 Release / 实际 Debug | 各 689/689 |
| 独立锁定包消费 | 55/55，62 原生 DLL 与 NuGet 一致 |
| 原生公开导出 | 41,075，与已验证 `.6` 包一致；无新增或删除 |
| 同发布窗口 | 桌面、工程图、F3h、F3g 四组全部通过，绑定日志空 |
| 视觉检查 | 19 张相关截图和 10 份单页矢量 PDF 逐张通过 |
| 外部读取 | FreeCAD 30/30 |
| 外部回写后 Cadoryx 重读 | STEP/IGES 20/20 |

WPF 冻结发布在 `artifacts/publish/f4-exchange-20261003-4`，CLI 在 `artifacts/publish/f4-exchange-cli-20261003-3`。17 个共享托管 DLL 与当前测试一致，实际 Debug/Release 测试原生 DLL 也与各自构建一致。汇总：`artifacts/f4-exchange-verification-1.json`；原始日志、TRX、文件及哈希均保留。旧 `.6` 丢面和 `.7` 四失败候选不作为最终通过证据。

外部检查使用本机已有 FreeCAD 1.1.3 / OCCT 7.8.1，独立于 Cadoryx 的 OCCT 8.0.1，并使用隔离配置。输入为毫米、英寸、米、旋转/颜色的 STEP/IGES 八个样本以及两个真实制造文档，共十个源。每个源实际导出三格式；STEP/IGES 经 FreeCAD 读、写、再读，再由 Cadoryx 精确重读及观察。文件、源和执行文件均绑定哈希。最终丢面样本保持 18 面、1436 mm²。

## 边界及继续工作

2026-10-14下一批准备：[独立源消费者](../tools/ExchangeOccurrenceProbe/Program.cs)已以精确最终87包编译，13托管/62原生载荷逐项匹配，[冻结收据](../artifacts/f4-occurrence-external-1/preparation.json)明确为NOT_RUNTIME。源契约要求一个装配根、两个共享子装配出现、五个零件出现、实际复用分区2/3、六个源Box面及各一个真实dimension/tolerance/datum；PMI引用源文档中实际测得的x=0/x=2面标签。[FreeCAD观测器](../tools/ExchangeOccurrenceProbe/freecad_observe.py)记录实际object/link/group、global placement、每个对象自身Shape及ShapeAppearance，再写回并重读STEP；原始对象数不当实例数，复合体/叶体不重复累计，link的Shape坐标与global placement分列，不猜对应。

[串行入口](../tools/ExchangeOccurrenceProbe/run.ps1)先要求第八候选完整两配置实际通过、原模型队列终态及当前原生消费者空闲，再分别执行Source/External/Returned；每轮新目录和退出记录保留失败。此时仅消费者编译、包字节核验、Python与PowerShell语法检查完成，三阶段均未运行，外部完整互操作未证明。最终87公开PMI快照已核对所有权：父绑定标签必须同文档，快照内辅助Shape须释放，标签Entry只在当前文档内有效；还缺从公开文档所有者读取“是否显式声明/每单位米数”的已验证路径，SDK会话按生成器补充或给出既有公开路径。PMI每项没有独立unit字段，STEP也没有被证明的notes专用开关，不将隐式mm或OCAF注释创建当作声明/交换成功。

本地观测预算为 256 个体实例、4096 个出现节点及 250000 面。面积与体积按实例累计，不扣除重叠；混合体、片体、线框及网格不推断闭合实体体积。颜色集合相同不证明逐面分配相同。

FreeCAD 这组外部回传使用 Part BRep 通道，会扁平化层级；没有证明名称、共享关系、逐面颜色或 PMI 保留。STL 明确按毫米解释，只核对三角形数及边界，面积为观测，未证明最大网格误差或闭合体积。边界、面积及体积一致不能代替完整几何等价或拓扑身份传递。

更完整的层级/面色/PMI 外部实测、网格误差、其他外部 CAD 以及长期兼容性继续保留在 F。接着实施 [F5 命令与 AI](F5_COMMAND_AND_AGENT_PLAN.md)，A/B/C/D/E 和进一步制造规则仍见 [总跟踪](AF_IMPLEMENTATION_TRACKER.md)。这些待办不会因为第七批暂缓而删除。
