# F5g 完整工程图作者工具

2026-10-04。本页记录 `cad_drawing` 和终端 `DRAWING` 对既有生产工程图能力的完整作者入口，以及 PDF 和模板文件输出。作者入口已接入；169 项联合检查和最终 2028/2028 全量通过，Release 零警告、零错误。最终四组窗口通过，孔表原失败保留，增加日志后的重放通过；Codex 八轮通过，LM Studio 两次失败。整个 [A–F 请求](AF_IMPLEMENTATION_TRACKER.md)继续，第七批实机、安装及交付暂缓。

## 作者能力

| 对象 | 创建和编辑内容 | 身份与生产核验 |
|---|---|---|
| 图纸 | 名称、纸幅、单位、标题、作者、ISO/GB/ASME、制造图框 | 创建由服务端分配 ID；编辑保留图纸内容；自定义字段和注释有独立增删入口 |
| 视图 | Front、Top、Right、Isometric、Section、Detail；位置、比例、名称、剖切平面、局部裁剪圆和剖面线 | 编辑保持类型、父视图和已选来源；`reselect` 明确更新来源；原生投影，父视图移动和比例变化会更新子视图 |
| 尺寸 | 长度、角度、半径、直径、文本位置、ARGB 颜色和上下偏差 | 输入仅含精确解析基准选择；实际尺寸由原生解析和生产测量生成；半径和直径只接受一个基准 |
| 制造标注 | 表面基准、完整圆柱尺寸轴、两面尺寸中面、控制框、序号和明细表 | 原始有限域、独立两面身份和原生尺寸证明重新核验；基准组保留顺序、共同基准及材料边界条件；数量和零件名称从实际实例生成 |
| 孔表 | 当前原始孔壁、行号、表位置、独立或关联坐标基准 | 新行服务端分配 ID；原有行必须复制本表真实 ID；尺寸、邻接证明、坐标和基准 revision 重新核验 |
| 图框内容 | 自定义字段、制造注释、分区 | 使用既有容量、日期、纸幅和重叠检查；空分区和空明细表位置可以明确解除，无变化不添加历史 |
| 模板库 | 从图纸保存、精确替换、应用、文件导入导出、删除 | 保留既有具名库检查；同名导入拒绝；导入重新分配模板 ID，应用为字段和注释分配新 ID，模型/视图保持原身份 |
| 刷新 | 全部图纸缓存刷新 | 缺失、分裂、合并或多路径历史继续失效；不会按外观或相近编号猜测附着 |

尺寸和制造对象的编辑保持原来的类型、图纸/视图所有者及附着。改变附着使用 `reselect`；改变孔表坐标关联使用明确重选或 `detach_coordinates`。取消、关闭和异步旧状态发生在提交前时，旧作者与资产保持；提交后的操作按真实成功回执记录。

## 查询与使用

先调用 `cad_drawing` 的 `action=contract` 查看类型和动作，再用 `contract,type=sheet/view/dimension/datum/control_frame/balloon/hole_table/field/note/parts_table/zones` 查看严格作者 schema。`contract,type=template` 提供具名库和文件契约。没有活动文档也可以查询 AI 契约。

`inspect` 按类型、图纸和对象 ID 分页，默认每页 8 项，最多 50 项。查询保留作者输入、实际状态、失效原因及另列的测量/来源观察数据；`items[].edit` 可以直接复制给工具。视图 stroke 和尺寸、孔、序号的缓存不是可编辑输入。`inspect_sources` 使用完整实例路径、实体 ID 和几何 revision，返回原始全拓扑 map 的槽位、fingerprint、asset 与可复用选择；包括没有生产特征的资产实体。

所有查询和作者动作使用真实 `documentId/stateId`。创建省略 `id`，编辑/重选/删除带真实 `id`。子对象还带 `sheetId`；尺寸和制造作者带实际 `viewId`。服务端不会用名字匹配身份。请求上限 131072 字符，`authorJson` 上限 65536 字符；终端保持 4096 字符的已有限制。

终端 `DRAWING CONTRACT [type]` 返回相同契约，JSON 动作可以用 `author` 对象代替 `authorJson` 字符串。终端自行捕获当前文档状态。以下创建不含客户端分配的图纸 ID：

```text
DRAWING {"action":"create","type":"sheet","author":{"name":"Manufacturing","widthMm":297,"heightMm":210,"unit":"Millimeter","title":"Bracket","standard":"ISO"}}
```

用返回的 ID 创建视图，再从 `inspect_sources` 复制原始选择创建尺寸或制造对象。图纸位置和尺寸使用 mm，角度尺寸观察值使用 degree；剖面线角度使用 degree。独立孔表 frame 的 `origin/normal/u` 是实例局部向量对象。工具不会接受任意 `localPoint`、`edgeParameter`、`coordinatesMm`、`value`、`quantity` 或 stroke 当作可信几何。

为了在默认上下文中保留全部 12 个工具，`cad_datum` 的供应商 schema 也改为合法字段集合，完整各类型结构通过 `cad_datum action=contract` 或终端 `DATUM CONTRACT` 发现。生产作者校验仍按实际动作和类型拒绝缺失、未知、重复和隐藏字段；短会话普通/激进预算保留全部工具，长会话按工程图请求优先保留相应工具。

## PDF 和模板文件

`cad_file action=pdf` 使用绝对 `.pdf` 路径，输出全部图纸，一张一页，复用既有矢量 PDF。任何失效视图、尺寸、制造标注、孔表或行都会拒绝输出。PDF 要求可编辑作者文档且在历史末端。

`cad_file action=template_export` 使用真实模板 `id` 和绝对 `.json` 路径，生成既有原生 v1 模板文件。`cad_drawing action=import_template,path=...` 使用同一有界原生文件读取器，导入继续检查模板内容和库名称。模板不携带模型、视图或拓扑。

两类输出均经 `CadDocumentSession.WriteFileAsync` 的保存队列、捕获快照和资产租约，再写临时文件、计算实际字节数与 SHA256，最后原子发布。默认不覆盖，覆盖须带 `overwrite=true`；取消发生在发布前不会替换原文件，发布后按真实回执返回成功。文件动作不新增撤销记录，也不把 PDF 创建当作完整外部标准符合性。

## 当前验证与后续缺口

联合证据 `artifacts/f5-drawing-focused-5/results.trx` 为 169/169，发生在最终字体修复前；字体修复后的全量 `artifacts/f5-drawing-full-2/results.trx` 为 **2028/2028**，零失败/跳过。Release `artifacts/f5-drawing-build-14.log` 为零警告、零错误。冻结发布 `f5-drawing-20261004-3` 与测试/探针的 12 个生产 DLL 相同，见 `artifacts/f5-drawing-binaries-2.json`；最终汇总为 `artifacts/f5-drawing-verification-1.json`。

新增测试覆盖六类视图、四类解析尺寸、圆柱/两面尺寸证据、制造基准与有序控制框、剖面线及局部视图、模板库和文件、关联孔表、无特征资产实体、原始历史延续/失效、无变化编辑、保存重开、PDF 回执、错误身份/缓存/枚举、迟到取消/关闭/旧状态和资产归零。默认工具预算和真实 HTTP schema 在联合范围。

最终候选桌面、工程图、C1 参数及 F3h 双面尺寸四组窗口 PASS，绑定日志为空；桌面通过实际终端 TextBox 与 Automation Invoke 创建三视图、直径尺寸、基准、控制框、孔表、注释、模板导出、撤销/重做、原生保存重开和 PDF。终端截图只捕获日志区域：选中回执上部可读，长 JSON 延续到截图下方。

同一轮 **F3c 孔表 FAIL**：首行加入时 `DrawingSheets.Values.Single()` 遇到多张图纸（原 line 66）。原结果保留于 `artifacts/f5-drawing-windows-2-f3c-holes/result.json`。检查没有放宽断言；增加按钮来源/提交快照日志后，诊断发布 `f5-drawing-20261004-4` 的独立重放 PASS，一张图纸、两行孔、三个坐标基准和三语言 PDF/原生重开成立。日志只有一次作者 AddSheet 点击及一次新增图纸提交，没有观测到非检查脚本点击。12 个生产 DLL 保持不变，只有 WPF 检查日志载荷不同，见 `artifacts/f5-drawing-diagnostic-publish-1.json`。**原失败根因尚未证明，重放成功不等于重复性问题已修复。**

PDF 视觉检查发现真实缺字：U+2300 直径符号在 Segoe UI/SimHei 中没有字形。已将技术符号范围交给 Segoe UI Symbol，测量与绘制使用相同字体；保留原符号，没有替换成近似字符。最终原候选八页及孔表诊断三页，共 **11 页矢量 PDF**，通过无光栅/无空 Unicode 映射检查并逐页检查中日文字、直径/公差、剖面线、控制框和孔表布局。见 `artifacts/f5-drawing-font-map-1.json`、`artifacts/f5-drawing-pdf-check-2.json`、`artifacts/f5-drawing-pdf-check-3.json`。实纸比例及完整标准符合性未验收。

实际 Codex app-server / gpt-5.6-luna 八轮工程图会话 PASS，原生文件/PDF输出、精确尺寸和资产归零有记录：`artifacts/f5-drawing-codex-2/result.json`。实际 LM Studio HTTP / prism-ml/bonsai-27b 两次尝试 FAIL：第一版第三轮、最终版第二轮超过 16 工具轮数；最终版成功创建 Top 后遗漏查询身份/类型，又把 documentId 当作对象 id 反复筛选。严格参数拒绝及原始 HTTP/工具回执保留于 `artifacts/f5-drawing-lmstudio-1`、`artifacts/f5-drawing-lmstudio-2`，没有宣称完成八轮。此前多文档五次失败独立保留；本次 Codex 通过不能代替 LM Studio 验收。

本次补齐的是已有生产工程图能力的作者入口。进一步制造规则、缺失算法历史、完整标准符合性与外部 CAD 对照仍按 [制造计划](F3_ASSOCIATED_MANUFACTURING_ANNOTATIONS_PLAN.md)和 [A–F 跟踪](AF_IMPLEMENTATION_TRACKER.md)继续；A 性能、B 复杂关系、C 多副本面色/历史/螺纹、D 曲面终止/偏移、E 复杂边链/半径规律及 F 外部元数据仍未被本项抹去。
