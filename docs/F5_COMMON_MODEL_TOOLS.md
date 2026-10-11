# F5e 常用建模命令与 AI

更新：2026-10-03。用户授权的 A–F 继续，第七批实机、安装及交付暂缓。常用建模作者接口已接入生产命令，最终170项联合专项通过，Release零警告／错误；同冻结发布的全量及十窗口证据正在收集。本页不代表整个 F5 或 A–F 已完成。

## 动作与身份

第九个工具 `cad_model` 使用实际 `documentId/stateId`，在生产会话 capture 和提交守卫内复验。修改要求原始特征 `id/revision`；新增要求实际本地 `partId/name`。作者 JSON 放在 `authorJson` 字符串中，按查询到的配方契约严格解析。文档状态、草图修订、来源结果、原始全 map 面身份失效时拒绝执行，不以名称或几何相似重绑。

| action | 行为 |
|---|---|
| `create` | 新建一项常用配方，显式消费的输入必须仍是实际显示输出 |
| `edit` | 保留特征类型、原 ID、有序输入及 Boolean／拉伸操作语义，更新合法作者字段及后继 |
| `reselect` | 明确重选失效抽壳／拔模面或拉伸精确终止面；新选择须经原生内核确认 |
| `detach_profile` | 解除草图轮廓／扫掠剖面与路径／放样站位来源，保留最后求得的几何 |
| `detach_datum` | 明确解除关联轴、孔框架、布局、终止或拔模中性面基准，保留最后局部值 |
| `set_assembly` | 完整 `path/name/authorJson`；省略 `id` 新建实例重复，有 `id` 修改原布局 |
| `detach_assembly` | 精确布局 `id`；解除布局，保留实际实例及镜像派生定义；参数依赖原子拒绝 |

装配布局没有持久化 revision，更新由文档 `stateId`、实际布局 ID、完整来源路径及原始 owner/source slot 共同守卫，不编造几何修订。装配坐标为 `parent_local`；零件建模坐标为 `part_local`，长度 mm、角度 rad。工作平面和关联基准的其他坐标契约继续见 [F5d](F5_ASSOCIATED_DATUM_TOOLS.md)。

## 配方

| type | 作者输入与生产命令 |
|---|---|
| `extrude` | 独立或实际草图剖面，正／反／对称／双向，距离／通切／平面或精确平面终止；NewBody/Fuse/Cut，`AddExtrusionCommand`只消费目标，不消费终止参考体 |
| `revolve` | 曲线、孔岛轮廓及角度，默认局部轴、明确局部有向轴或实际关联轴；`AddBodyCommand` |
| `boolean` | Fuse/Cut/Common及完整有序特征／结果修订；重复输入拒绝，`BooleanCommand`保留消费语义和输出属性 |
| `hole` | 入射平面、局部位置、通／盲孔、普通／沉孔／沉头、现有公共度量间隙标准；`AddHoleCommand` |
| `repetition` | 镜像／线性／圆周、Compound/Fused，完整布局及副本身份；`AddRepetitionCommand` |
| `hole_pattern`、`extrusion_pattern` | 只提交实际种子和布局，模板从生产种子读取；不允许伪造模板或复制钻孔基体，复用对应重放命令 |
| `sweep` | 独立或关联剖面与显式有向开路径、Solid/Sheet和拐角规则；关联路径保留实体次序与反向旗标 |
| `loft` | 作者站位顺序和完整通道 ID／parent，孔岛按明确父子通道组织，独立或实际草图剖面；站位与剖面平面必须一致 |
| `shell` | 原始精确去除面、壁厚及内外方向；`AddFaceFeatureCommand` |
| `draft` | 独立面程序 ID、精确面、角度、中性平面、拔模方向和传播；`AddFaceFeatureCommand` |
| `assembly_pattern` | 镜像／线性／圆周、完整来源实例路径及布局；复用 `AssemblyRepetitionCommands` 和镜像派生／参数／后继更新 |

编辑通过 `RecomputeCommand`；精确面重选通过原有重选和原生确认路径。原始输入次序、被消费的实体、存活副本／实例 slot、种子、通道与程序身份保留。省略副本／slot数组时，生产 resize 保留存活身份并只为新增项生成 ID；显式替换存活身份被拒绝。新增放样通道和拔模程序可省略 ID，编辑必须显式提供。模型结果列出实际新增 feature/body/part/copy/channel/program；装配结果列出实际新增 pattern/definition/feature/body/slot。

相同作者输入返回 `changed=false`，不创建撤销记录；复制旋转 frame 时只有输出原点与两条作者轴完全相等才保留旧四元数位，不使用几何相似容差。失败、关闭、旧状态、提交前取消保持旧快照且回收临时原生资产；提交后的取消仍返回真实成功。只读／回滚、锁定图层、驱动参数和基准不能通过该接口绕过。

## 查询和有限输入

`cad_inspect.recipes` 按零件特征顺序输出作者输入、实际输入结果修订、显示／失效状态和可直接复制的 `edit`。不支持此接口的配方给出诊断；失效的精确面配方提供 `reselectTemplate`，旧面身份须由调用者明确替换，不能当作当前来源使用。

`sketch_geometry` 输出实际草图 revision、平面及完整原始点／线／圆／弧／Bezier／Spline／Ellipse／Freeform身份；选闭合轮廓和有向路径必须明确实体次序。作者 codec 支持原有平面曲线、显式结点、孔岛与 Regions，不按坐标猜孔对应。关联分支只接受草图／基准身份，独立分支才接受作者坐标，不能同时携带计算缓存。

`assembly_patterns` 返回完整来源路径、存活 slots及实际镜像派生定义。共享父装配来源有多个路径时全部列出且不生成可编辑单路径，调用者需先使父装配独立。

`model_contract` 以 `id=type` 查询 JSON schema，每页最多5500字符；用 `nextOffset` 请求下一页并把 `$defs` 合并到同一根 schema。`schemaComplete=false`明确表示引用定义仍在其他页，不能将一个局部页面当作完整 schema。这样契约不会被普通6000字符工具结果上限截断。根作者条件、实际合法几何和身份／依赖条件仍由生产命令核验。

工具请求总预算32768字符，终端沿用4096字符；纯作者表数组预算1050、嵌套深度24，生产配方另有自己的边数／孔／通道／站位限制。未知或重复字段、缺少坐标、空 GUID、错误类型、数字枚举和隐藏 companion 被拒绝。原生文档 schema及SDK版本不变。

## 终端

```text
MODEL CONTRACT extrude [offset limit]
RECIPES [offset limit]
SKETCH_GEOMETRY [offset limit]
ASSEMBLY_PATTERNS [offset limit]
MODEL {"action":"create","partId":"实际零件GUID","name":"Profile","author":{"type":"extrude","section":{"profile":{"points":[{"x":0,"y":0},{"x":4,"y":0},{"x":4,"y":3},{"x":0,"y":3}]},"frame":{"origin":[0,0,0],"normal":[0,0,1],"u":[1,0,0]}},"distance":2}}
```

终端允许 `author` 对象作为便利写法，转换为同一 `authorJson` 输入；两者不能同时提供。终端上下文自动捕获身份，不在 JSON 中重复 document/state。复制 AI 查询的 `edit`时，去掉这两个身份字段后可直接用于终端。原有非 JSON 命令的引号规则保持。

## 当前验证

最终170项联合专项通过，覆盖十二类作者契约、原生几何测量、正反双向和精确终止、度量标准孔、旋转／曲线／孔岛、关联有向扫掠、放样父子通道、镜像与阵列存活身份、实际装配路径及镜像派生定义、重选失效面、参数与工程图后继、撤销保存、schema分页与复制、只读／锁定／回滚和无变更。真实原生评估及精确面确认分别覆盖取消／旧状态／关闭；资产回收及提交后取消都有断言。

冻结发布 `artifacts/publish/f5-models-20261003-1`；八个生产程序集与全量开始时一致。最终全量1910/1910通过，Release零警告／错误；同冻结发布十窗口PASS，八个生产DLL与测试哈希一致，绑定日志为空。实际TextBox／Execute Automation Invoke验证拉伸24→60 mm³、撤销／重做、孔重放984.2920367320512 mm³、两个装配实例slot及保存。四张截图已检查；长JSON仍延续到输出视口下方，其余窗口截图本轮未逐张复检。汇总见artifacts/f5-model-verification-1.json。供应商请求和长会话仍在 F5f 单列，物理指针及第七批验收暂缓；其余 A–F 缺口继续见 [跟踪](AF_IMPLEMENTATION_TRACKER.md)。
