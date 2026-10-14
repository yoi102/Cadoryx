# D7 解析有限曲面的完整近、远分支候选

2026-10-14，status265：本候选已按[根接入审计](../capability-evidence/diagnostics/af1082-f5-d7-local87cu1/root-integration-verification.json)接入开发根，精确本地87.cu1 NuGet及30锁保持非OCCT依赖；1200原输入加唯一恢复语言差异、locked restore/两配置完整build0、SDK13/native62及根Release原缺失分支36/36已核对。原完整7117两配置和243矩阵属于此前冻结候选，根完整7117没有重跑。最终WPF恢复与MiniPC两次直接性能/两次Open已有专项证据；原D6实际窗口未通过、材质窗口仍待，不把根合入当作D批或A–F完成。以下“未合入”保留历史当时状态，以本条及STATUS为准。

2026-10-14。A–F继续；原24严格STEP按精确名单延期，G和清理暂缓。
生产仍为1082/公开Preview.85。本候选使用开发期允许的精确本地
`8.0.1-preview.87.cu1` NuGet，尚未合入生产。

## 已通过的算法专项

原99有限曲面用例加新增144，两配置各243通过、零失败、零跳过。
[完整终态与载荷审计](../capability-evidence/diagnostics/d7-analytic-branch/revision4-verification.json)
核对原用例名称、全部断言、公差和实际62个原生模块。

新增108正例覆盖球面圆轮廓、柱面多边形轮廓，各三种位置/姿态，
普通材料、孔、递归岛，以及加料/去料/新实体的Near/Far分支。
独立解析体积、精确有效性和自交、原作者和终止资产字节、撤销重做、
全新资产存储中的保存重开均通过。36负例确认缺少所选半域时不制造材料、
不改变文档/历史/资产。专项不能证明任意自由曲面或全部轮廓组合。

第三版各207/243的36个柱面孔/岛起始单位棱柱偏差4e-7，在第四版消除。
原1e-7面积预算保持，原失败日志保留，没有改为跳过或降低要求。

实现从有限UV域内求解析系数，在私有深复制上构造明确近/远半域；
原有限面继续作为作者身份、盖面和边界覆盖证明。边界覆盖依次扣除原允许
面域的并集，避免混合compound的单次Common漏掉覆盖。

起始材料使用原SectionWire和明确逆向孔线框，通过生成器提供的
`PlanarWireFaces.Create`直接造面，再单位拉伸。递归岛独立构造；
没有将曲线拟合为另一表示、使用3D孔切刀、猜测作者身份或放宽公差。
起始平面必须完全先于解析入口；穿越、内部起始及任意自由终止曲面仍需
独立契约。拓扑历史未获证明时继续明确要求重新选择。

## SDK接收与整应用准备

[包字节接收](../capability-evidence/diagnostics/d7-analytic-branch/sdk87cu1-package-reception.json)
核对14包、managed13/native62及2431来源；
[终态接收](../capability-evidence/diagnostics/d7-analytic-branch/sdk87cu1-terminal-reception.json)
核对1841冻结资格来源、上游两配置Runtime2367/Generator437、干净NuGet消费者
facade53/modules31的完整TRX和各实际native62。源码Debug使用Debug TBB，
包消费者使用Release原生；相同ABI1.118/bridge0.133.0不表示包字节相同。
API和冷生成声明保留上游收据，不能据此宣称整个SDK迁移批次完成。

[整应用工具](../tools/FiniteSurfaceProductCandidate/README.md)在隔离目录复制当前
1194输入，加D7后1198输入，50项目恢复和锁定恢复通过，非OCCT依赖保持；
完整solution两配置编译零警告/错误，实际各发现6991项，包括原6830、旧入口
反馈17及分支144。WPF/Tests的SDK13/native62和模型探针共享16一致。
此前组合完整6991两配置均已终态，各6967通过、精确原24延期失败、零跳过，无新增失败；实际名称/源码/native62已核对。包含最新F5反馈的[统一候选](AF_F5_D7_UNIFIED_CANDIDATE.md)各7117完整回归现已启动但尚未全部终态，不能用旧6991代替新组合。

首版候选class-library输出漏复制NuGet运行时模块，启用标准
CopyLocalLockFileAssemblies后写入新的build2目录；缺失载荷未运行。
接收工具先误假设Debug TBB名称与Release相同、kernel只解析13包，随后依据
实际构建分别严格核对配置名称和完整14包集合；没有放宽产品断言。

## 后续门禁

最新F5+D7组合完整7117两配置、D7生产接入及最终载荷验收仍待；随后继续原36 D6窗口、
24/36恢复、材质实际UI、MiniPC性能/生产打开和真实Bonsai两场景。
原Far90中的18个SDK原始完整面Near体积失败仍保留，不扩大为其他交换问题
豁免，也不将它们的原始诊断当本候选实际通过证据。

[工作区对象过滤诊断](../capability-evidence/diagnostics/af1081-local86/workspace-document-filter-feedback-diagnostic.json)
确认旧模型把documentId用作对象过滤id后得到空页，而同一状态无过滤有对象；
F5身份反馈已合入开发，尚未证明它是工具轮预算失败的唯一根因；当前原两场景按完整门禁排队重跑。F批继续保持原模型、
8192上下文、16工具轮和每轮4分钟条件。

正式已验子批仍A4/B17/C5/D5/E4/F5g；D7专项和SDK交付均不代表D或A–F完成。
