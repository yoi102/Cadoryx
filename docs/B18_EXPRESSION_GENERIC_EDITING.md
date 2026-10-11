# B18 表达式通用编辑与点来源

2026-10-07。本阶段是 B18 的有限通用编辑子阶段，A–F 仍在进行，G 暂缓。正式已验收批次保持 A4 / B17 / C5 / D5 / E4 / F5g。

## 当前实现

生产 `TrimCurve` / `ExtendCurve` 已接表达式目标，`OffsetCurve` 已接 Bézier、样条、椭圆、自由曲线和表达式源。交点枚举覆盖旧直线、圆、圆弧、Bézier、样条及椭圆、自由曲线、表达式，因此 Bézier/样条/表达式编辑可使用这些有限边界。精确偏移按现有开放曲线工具的有向左侧转换到表达式右法线契约，保存完整 source geometry 和关联路径；原直线/圆/圆弧偏移路径保持。

单段保留曲线作者 ID；改变的端点及结构方程使用新 ID，未改变的外端保留明确原 ID。中段拆分产生两个新曲线作者和新结构方程，不把旧 ID 分给任一片段。未冻结的局部表达式依赖在来源被拆分删除时整笔拒绝；用户明确冻结后保留原值，不按相邻坐标自动重绑。

延伸使用完整原表达式上的单层 Continuation，保留原 native 参数单位和方向，查询最近有限边界，并在发布候选前认证原生正则性。只有同 source/entity/family/captured basis、原树完整不变、单层明确扩展且非冻结的作者操作可以增加 BasisDepth；求解器仍按完整 SameTable 核对，不能借此增加作者层。原 (0,4] 延伸量、64 层作者预算及 256 变量 / 512 方程 / 80 迭代不变。

整周期椭圆/圆和有明确周期定义的 B-spline 使用单独的周期补区间证明处理跨接缝修剪，保留完整 basis。普通延伸的包含区间守卫没有扩大成任意 wrapper 许可。闭合曲线不制造可延伸端点。

`SketchPointOrigin` 保存 Unknown / Independent / CurveGenerated。自动清理只删除本次旧点集合中明确生成且已无任何 typed 几何或投影引用的点。独立点、未知旧点、共享 primitive/fixed/expression 端点及其用户约束保留。基本作者、精确端点、编辑、原生投影、loop offset 和圆角生产的新点均明确记录来源；原点 ID 的来源不可被编辑或注入求解器重解释。

## 实际修复的失败

- 旧版本迁移补草图 revision 时，当前 DTO 提前写出了点来源字段；中间声明仍旧格式，最终迁移因此拒绝。中间写出恢复真正四字段点，最后的来源迁移才写五字段。冻结 M4S1 夹具未修改；人工旧夹具明确移除新字段，未放宽新格式校验。
- 反向修剪在保留原端点时，`Last - span` 会落到原有限域外一个浮点单位。作者层直接保留已声明端点；SDK19 适配器按保留端点选择修剪时的临时方向，再恢复原方向。未扩大公差或夹紧任意坏区间。
- 表达式与整圆求交曾用三个点还原整圆，首尾重合导致拒绝；整圆改用明确中心、半径及角域的精确椭圆表示。
- 三语言生产工具名和提示同步为曲线编辑；区分直接圆/圆弧增大半径与精确曲线的有向左侧、来源关联。

## 存储与验证

writer **0.7.5** / document **57** / sketches **22**，点数组五字段。真实旧四字段点保留 ID、坐标和 construction，来源明确为 Unknown；不猜归属。未知枚举、字段缺失/多出、各自降版及新点字段夹带旧 document 均拒绝。表达式 JSON3 / MP4、geometry1、features43 及 17 分区保持。

新增来源专项 **17 项**，表达式通用编辑专项 **73 项**。最终文案候选 Release 与 Debug 联合各 **1919/1919 通过**。两配置构建均零警告/错误。新发布候选 `artifacts/publish/b18-expression-edit-2` 的实际中英日文修剪/拆分/延伸/偏移共 **12 组窗口通过**；生产 ToolPicker 和 Canvas.PointerPressed、偏移字段、单步作者及文档 undo/redo、preview/confirm/cancel、主文件重开均验证，12 张完整 PNG 逐张复核，bindings 空，关闭后资产归零。按钮使用 WPF Automation Invoke/Toggle；桌面与物理输入分列，不能把 Canvas 函数调用当物理鼠标验收。

原896源码候选完整 Release 首次已终态：**3745通过 / 12失败 / 0跳过 / 3757总计**。12项均为旧迁移夹具残留 sketches22 点来源，触发严格旧版本拒绝；该首次完整回归未通过。12个明确旧夹具已转换为四字段点载荷，原文件字节（除两行插入）和断言保持，不改生产校验。随后包含区域基础105项的新904源码候选Release/Debug联合各2036通过；同冻结904源码完整3862/3862已通过，0失败/0跳过，见[区域基础](B18_EXPRESSION_REGION_FOUNDATION.md)。首次失败保留在 `b18-expression-edit-full-failure-verification-1.json`。此前 **3667/3667** 属于 status72 冻结887消费者候选，不证明当前修改。当前圆盘另取得7次静止光标下的桌面像素证据，约46.528–51.302ms检测到页面，随后移鼠不改页；同冻结发布DLL及实际62加载匹配。当前完整 Debug、一般/完整窗口和物理键鼠未验。

证据入口：`artifacts/b18-expression-origin-related-2`、`artifacts/b18-expression-generic-edit-related-7`、`artifacts/b18-expression-generic-edit-debug-related-2`、`artifacts/b18-expression-edit-wpf-2`。第一次迁移25项失败、表达式4/2项失败、编译失败和夹具错误记录均保留。

## 同批继续

本阶段没有完成全部通用编辑契约。直接椭圆/自由曲线的通用修剪与延伸、旧 line/arc/circle 对新 typed 边界的双向编辑、各家族/周期/重复结点的完整点击矩阵、C1 后继实体与孔岛/恢复，以及区域 Miter/Round/Bevel 整组来源配方继续。

区域不能仅给每条 Parallel 保存关联：源变更后，交点参数与桥段必须一起重算。下一阶段按 [区域来源设计](../artifacts/b18-expression-region-author-mapping-next-1.md) 完成 source loop/index/direction 映射、整组参数与存储、两符号/两绕向/多根/四层孔岛、C1 后继和三语言生产控件。SDK 作者值对应关系不作为 native topology history。

一般曲线扫掠与变化截面放样、其他关系作者窗口、一般交换、偏移全过程单射性和原生拆分/合并历史及其余 A–F 继续；不将本阶段提升为 B18 或 A–F 完成。
