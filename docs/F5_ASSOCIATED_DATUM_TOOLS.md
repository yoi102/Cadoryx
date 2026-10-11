# F5d 关联基准命令与 AI

更新：2026-10-03。第 1–6 批继续；第七批实机、安装及交付暂缓。关联基准的终端／结构化作者入口已实现，最终 154 项联合专项通过；1827项全量及同冻结发布六窗口通过，八个生产程序集哈希一致。本页不代表全部 F5 或 A–F 完成。

## 作者入口

`cad_datum` 要求实际 `documentId`、`stateId`。新增平面、轴、点省略 `id/revision`；修改必须同时提供原始 ID 和当前基准 revision。不能改变所有者或把原 ID 改成另一类基准。作者坐标为零件局部 mm，角度为 rad。

| action | 输入与行为 |
|---|---|
| `set_plane` | `partId/name/author`；Offset、Midplane、Angle，来源覆盖 Basis、Datum、BoxFace、Independent、ExactFace、AxisNormal |
| `set_axis` | Basis、TwoPoints、PlaneIntersection、PlaneNormal、Analytic、Independent；可用原点、反向及沿轴偏移 |
| `set_point` | Independent、Offset、Midpoint、ThreePlanes、AxisPlane、CircleCenter |
| `delete` | `id/revision`；有草图、工作平面、参数、基准或特征依赖时拒绝删除 |
| `detach` | `id/revision`；改成独立作者输入，保留最后求得的局部平面、轴线或点，不再追随来源 |
| `bind_sketch`、`unbind_sketch` | 精确 `sketchId/sketchRevision/datumId/datumRevision`；只允许同零件平面，解除保留最后应用平面 |
| `bind_workplane`、`unbind_workplane` | 精确 `datumId/datumRevision/path`；完整实例路径确定世界位姿，解除保留最后工作平面 |
| `inspect_sources` | `featureId/revision`、可选 `offset/limit`；读取原始 BRep 全拓扑 map 的一个页面 |

所有设置复用 `SetDatumsCommand` 和参数／草图／原生特征／工程图更新，在生产会话中一次提交。已有不同草图或工作平面关联必须先明确解除；同一作者输入或关联返回 `changed=false`，不生成多余撤销记录。提交成功后取消仍返回真实成功。只读或回滚中的作者修改被拒绝。

## 原始拓扑来源

`inspect_sources` 的分页范围是原始全 map 槽位，包含不支持的槽位及明确诊断；不会把筛选后的位置冒充原始 index。偏移 0–100000、页面 1–50；超过既有精确 index 预算的源模型明确拒绝。每个可支持的面／边包含完整 `selection` 和内核读取的解析几何。Plane、Cylinder、Cone、Circle 已由真实来源样本覆盖。

创建或重选精确来源时复制返回的 `selection`，工具只接受来源身份，不接受客户端的半径、点、方向或缓存。选择必须匹配实际 feature、part、revision、asset、fingerprint、原始 index、形状类型和 adapter。生产命令在守卫 capture 内重新读取几何，随后重算再次原生确认。改变、抑制或失效的源不能按旧槽位重用，也不会按相似几何重绑。

`cad_inspect.datums` 保留既有作者来源和评估缓存，并增加 revision、局部坐标说明、草图／工作平面消费者，以及可直接提交的 `items[].edit`。这个输入带实际文档／状态和原始对象身份。`datum_associations` 单独分页返回草图修订、基准当前及已应用修订、实例路径和最后平面，草图标明 `part_local`，工作平面标明 `world`。

平面作者 frame 使用 `{origin:[x,y,z],normal:[x,y,z],u:[x,y,z]}`。复制查询输出时，原点和两条作者轴完全相等才保留原四元数，避免表示转换的舍入产生无意义历史；这不是几何相似匹配。输入有变化仍执行原有构造和合法性检查。

## 终端

`DATUM` 接收完整 action JSON，文档／状态由当前终端上下文捕获，不在 JSON 中重复提供。JSON 的引号、空格和嵌套对象按原文解析；原有命令名称的引号规则保持。

```text
DATUM {"action":"set_plane","partId":"实际零件GUID","name":"Top + 2","author":{"kind":"Offset","primary":{"kind":"Basis","basis":"XY"},"value":2}}
DATUM {"action":"set_point","partId":"实际零件GUID","name":"Center","author":{"kind":"Independent","value":[1,2,3]}}
DATUM DELETE id revision
DATUM DETACH id revision
DATUM BIND_SKETCH sketch-id sketch-revision datum-id datum-revision
DATUM UNBIND_SKETCH sketch-id sketch-revision datum-id datum-revision
DATUM BIND_WORKPLANE datum-id datum-revision exact-path
DATUM UNBIND_WORKPLANE datum-id datum-revision exact-path
DATUM SOURCES feature-id revision [offset limit]
DATUMS [offset limit]
DATUM_BINDINGS [offset limit]
```

复杂作者输入按工具 schema 的对应分支填写，更新再加入 `id/revision`。工具总输入预算 16384 字符，终端沿用 4096 字符预算。未知或重复字段、隐藏 companion、错误类型、空 GUID、非法尺寸／相交和依赖环都被拒绝。

## AI 上下文

参数、历史和基准 schema 使用标准 `$defs/$ref` 复用字段定义，实际解析约束保持。默认 8192-token 短会话原来为历史固定预留比例，会裁掉新增基准工具；现仅把实际未用的短历史空间分配给工具，输出／安全／最小历史预算保持，长会话继续使用原比例及压缩。专项检查确认原有工具及基准工具保留、当前用户请求保留、正常及重试估算均在预算内。真实供应商会话仍在 F5f 单列。

## 当前验证

Release 构建零警告／错误，最终 154 项联合专项通过。覆盖所有作者构造、原始 Plane/Cylinder/Cone/Circle 分页和选择复制、缓存伪造／指纹拒绝、参数驱动、草图及原生拉伸／工程图后继、实际旋转实例工作平面、保存重读、撤销重做、删除依赖、旧状态／修订、只读／回滚、错误 schema、无变更结果及提交后取消。

异步边界分别用原生来源读取、原生后继重算和只读分页证明：取消／状态改变／关闭后不能返回可用旧身份或提交晚结果；新建原生资产回收，原快照保持。关闭引发的会话代际递增单独保留，不能误判成模型提交。

最终全量1827/1827通过，同冻结发布桌面／C1／C2／E1／制造基准／工程图六窗口通过，六份绑定日志为空；八个生产程序集与全量开始时及冻结发布哈希一致。实际终端控件完成平面新增／编辑、草图及工作平面关联、Z=5后继更新、撤销重做和解除保留最后平面，并读取6个Box原始平面。已检查11张最终截图；C2主窗口滚动区可能让顶部New按钮部分出视口，未宣称全部截图无裁切。冻结汇总为 `artifacts/f5-datums-verification-1.json`；物理指针和真实供应商请求未运行。F5e 配方与常用建模、F5f 文件／工程图／交换及模型长会话随后继续；第 1–6 批其他未完项仍见 [A–F 跟踪](AF_IMPLEMENTATION_TRACKER.md)。
