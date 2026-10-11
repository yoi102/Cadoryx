# F5g 完整工程图作者工具实施清单

2026-10-04。接续 [多文档工具](F5_WORKSPACE_TOOLS.md)，复用既有生产工程图命令、原生附着核验与 PDF 服务。作者入口已接入，169 项联合及最终 2028 项全量通过；四组最终窗口和孔表日志重放通过，原多图纸失败根因未证明，LM Studio 两次失败继续跟踪；完整记录及本次冻结验证状态见 [工程图作者工具](F5_COMPLETE_DRAWING_TOOLS.md)。整个 A–F 目标保持进行中，第七批实机、安装及交付暂缓。

## 作者操作

| 范围 | 实际生产入口 | 已接入的作者契约 |
|---|---|---|
| 图纸 | `TechnicalDrawingCommands.AddSheet/EditSheet/DeleteSheet` | 完整名称、纸幅、单位、标题/作者、ISO/GB/ASME、制造图框；修改保持真实图纸 ID，删除明确检查现存对象 |
| 视图 | `AddView/EditView/ReselectView/DeleteView` | Front/Top/Right/Isometric/Section/Detail；明确来源实例路径、父视图、切面法向/偏移、局部圆心/半径与比例；原生投影，不接受客户端 stroke 缓存 |
| 尺寸 | `AddDimension/ReselectDimension/MoveDimension/SetDimensionTolerance/DeleteDimension` | 完整一/二解析基准、位置、长度/角度/半径/直径、颜色及上下偏差；实际测量值由命令确认，不能接受任意缓存值 |
| 剖面线 | `SetSectionHatching` | 默认规则、材料规则、纸面线距与角度、启用/删除；父剖切与 Detail 后继原子刷新 |
| 制造标注 | `DrawingAnnotationCommands` | 表面/完整圆柱尺寸轴/完整两面中面基准；控制框、序号与明细表；保留有序/共同基准、实际附着和独立两面身份 |
| 图框内容 | `DrawingTemplateCommands` | 自定义字段、制造注释、分区；创建/替换身份明确，图框容量、三语言/日期和合法枚举沿用生产检查 |
| 模板库 | `Save/Apply/Import/Delete` | 完整作者模板查询、保存、应用、导入/导出文件；不接受任意视图/测量缓存，导入仍重新生成模板身份 |
| 孔表 | `DrawingHoleTableCommands` | 明确原始圆柱壁、完整孔证据、固定/关联基准坐标、行身份、位置、修改/删除/重选/解除；不得按外观或编号自动匹配孔 |
| 刷新 | `TechnicalDrawingCommands.Refresh` | 使用真实文档 state；缺失/歧义历史保留失效标识，不能把刷新当作自动猜测重绑 |
| PDF | `TechnicalDrawingPdf.Write`、`DocumentFileService` | 完整作者工程图矢量 PDF，经保存队列捕获、临时文件、取消和原子发布；回执返回实际字节数、SHA256、来源 state，与外部标准符合性分开 |

## 查询与身份

新增工程图专用工具采用可发现的作者 `contract` 和分页查询，避免把整个复杂 schema 塞入供应商参数表。每个类型提供严格作者结构及可复用编辑输入，清楚区分原始作者、解析证据和二维投影缓存。查询覆盖图纸/视图/尺寸/制造标注/孔表/模板/内容，保留完整 ID、实际 state、原始附着和失效原因。

修改、选择、重选及文件输出均要求真实 `documentId/stateId`。视图/尺寸没有独立 revision 时使用文档 state 与对象 ID；修改保持原来的所有者/类型，不以换 parent 或附着来源隐含改写依赖。创建 ID 在服务端分配并返回；需要显式父子/行对应的列表保留其真实身份。

`IDrawingTopologyResolver.ResolveDrawingTopologyAsync` 按完整实例路径、实体 ID、原始全 map 槽和 fingerprint 重建实际附着；参数表不能接受任意 `localPoint` 或 `boundaryEdgeIndex` 当作可信几何。圆柱尺寸、两面中面、孔证据分别复用 `IDrawingSizeResolver`、`IDrawingPlanarSizeResolver`、`IDrawingHoleResolver`。若来源带原始历史，保留 author selection 并通过 `IDrawingTopologyHistoryResolver` 证明当前延续；分裂、合并、多个路径及缺失映射均拒绝猜测。

## 提交与验证

所有作者编辑继续经 `CadDocumentSession.ExecuteAsync` 的真实命令、资产租约与图纸后继更新。取消/关闭/异步旧状态发生在提交前时保留旧作者；已提交的操作仍返回成功。只读或回滚预览不得用于作者编辑/PDF；原生文档保存继续允许保存完整作者。

验证至少覆盖完整建模→投影→制造标注/孔表→修改来源→证明延续或明确冻结→保存/重开→矢量 PDF 链路，错误身份/枚举/缓存、原生迟到取消/关闭、无变更、撤销/重做和资产归零。终端真实控件与 AI 直接工具分别取证；默认上下文预算及真实 LM Studio/Codex 请求继续复验。供应商的提示辅助成功、重复执行及失败记录分别保存，不把一次成功写成任意自然语言可靠性证明。

随后继续 [A–F 跟踪](AF_IMPLEMENTATION_TRACKER.md)中的性能、复杂草图关系、面色/原生历史/螺纹、曲面终止/偏移、复杂边链/半径规律、进一步制造及外部元数据。以上工具入口补齐不抹去这些原有缺口。
