# F5 命令状态、取消与参数工具

更新：2026-10-03。F5a 状态与取消、F5b 参数及作者查询已验证；F5c 特征操作已完成本地验证。整个 A–F 请求继续，第七批实机、安装及版本交付暂缓。

## 已实现的入口

终端和 AI 的 `cad_command` 将取消令牌传到生产命令。终端增加停止按钮，运行时 Escape 取消当前执行；切换绑定文档会取消旧执行。取消发生在提交前时，晚返回的原生结果会释放，旧作者状态与撤销历史保持；已经提交成功后收到取消，仍返回真实成功。

`cad_command` 在有活动文档时要求实际 `documentId`、`stateId`。终端上下文捕获当前身份；生产会话在锁内、Prepare 前再次检查，撤销和重做也在排队回调的锁内检查。关闭、旧状态和错误身份无法绕过这一守卫。无活动文档时，HELP 等原有入口保留。修改还受只读、回滚及现有生命周期/代际守卫限制。

新增作者查询：

| `cad_inspect.area` | 返回内容 |
|---|---|
| `parameters` | 参数 ID、名称、量纲、表达式或作者值参考、依赖、求值结果、单位及驱动绑定 ID |
| `parameter_bindings` | 精确绑定 ID、参数 ID、完整目标及当前作者值/单位 |
| `parameter_targets` | 当前可驱动的完整字段身份、量纲、值与现有绑定；`id` 筛选 owner |
| `datums` | 平面/轴/点、所有者、原始作者来源、修订、缓存及失效状态 |
| `timeline` | 同一零件的实际顺序、特征 ID、结果修订、输入及抑制/失效状态；必须提供 `partId` |

查询页面最多 50 项，偏移 0–100000，排序使用固定身份及明确序号。长度为 mm、角度为 rad、无量纲为 1。时间线返回作者状态并单独标明会话回滚游标，不把临时显示结果当作作者修改。参考参数读取设计字段，不测量或猜测相似几何。

## 参数编辑

新增 `cad_parameter`，所有动作要求当前文档及状态 ID：

| action | 输入与行为 |
|---|---|
| `set_expression` | `name`、`dimension`、`expression`；无 `id` 新增，有 `id` 更新该参数 |
| `set_reference` | `name`、`dimension`、完整 `target`；创建或更新作者值参考 |
| `bind` | `parameterId`、完整 `target`；不同现有驱动要求先明确解除，相同驱动返回无变更 |
| `unbind` | 绑定 `id`；保留最后应用的设计值 |
| `delete` | 参数 `id`；有驱动或表达式依赖时拒绝删除 |
| `recompute` | 不接受额外作者字段，走现有参数重算路径 |

量纲枚举为 `Scalar`、`Length`、`Angle`。表达式沿用已有 2048 字符预算、单位与函数契约。工具拒绝未知、重复字段、错误类型及空 GUID；更新不会把不存在的 ID 当作新增。参考参数不能作为驱动。目标按 `Feature`、`Datum`、`AssemblyPattern`、`SketchConstraint` 或 `ExactSketchCurve` 解析；草图约束要有 constraint，曲线要有 curve 及必要 index，隐藏或多余 companion 拒绝。

可直接复制 `parameter_targets.items[].target`；输出省略空 companion。不能通过名称或形状相似推导目标。所有变更复用 `SetParametersCommand`、原生内核、草图求解、基准和后继/工程图更新，在一次生产会话提交中完成。结果返回实际 stateId、changed 及真正创建的 ID；重复设置或绑定不虚报创建。

终端新增：

```text
PARAMETERS [offset limit]
BINDINGS [offset limit]
TARGETS [offset limit]
DATUMS [offset limit]
TIMELINE part-id [offset limit]
PARAM SET NEW|id name SCALAR|LENGTH|ANGLE "expression"
PARAM DELETE id
PARAM BIND parameter-id kind owner-id property [companion-id [index]]
PARAM UNBIND binding-id
PARAM RECOMPUTE
```

所有 ID 取自实际查询结果。`PARAM SET NEW width LENGTH "30 mm"` 新建参数；绑定 Feature 的 X 时，使用该参数 ID 和真实 feature ID。`set_reference` 当前通过结构化工具提供。终端数字采用固定文化，不受界面语言影响。

## 验证与边界

F5a：Release 0 警告/错误，44 项命令/AI 联合专项、1744 全量通过；冻结发布 `artifacts/publish/f5-state-20261003-2` 的桌面、E1 历史和 C1 参数三个窗口门禁通过，绑定日志为空。三语言取消控件截图已逐张检查。停止按钮使用实际 WPF Automation Invoke 与可控异步 executor；真实 OCCT 晚返回取消另由原生集成用例证明，不能把这两种输入混为一项。

F5b：最终 130 联合专项、1771 全量、Release 0 警告/错误通过。冻结 `artifacts/publish/f5-parameters-20261003-3` 的桌面/C1/E1 三个窗口门禁通过，三份绑定日志为空；四个相关生产程序集与全量运行前已写出的二进制一致，新增绑定单位断言另在最终专项核验。覆盖实际参数驱动几何及工程图、撤销重做、保存重读、索引曲线及草图约束身份、参考参数、依赖删除拒绝、重复操作、非法 schema、原生晚结果的取消/旧状态/关闭和提交后取消。

实际终端 TextBox 绑定及执行按钮 Automation Invoke 创建并绑定参数，原生 Box 从 24 mm³ 更新为 72 mm³，撤销/重做和保存通过；三语言取消按钮的可访问名称和提示已核验。截图检查发现默认日志行选中背景会出现白底白字，旧候选的自动检查通过不代表视觉验收通过。F5c 已修正容器背景，最终三语言取消及参数日志行四张截图均已检查，选中行文字可读。F5a/b 冻结汇总为 `artifacts/f5-command-verification-1.json`，不能覆盖后续 F5c 源码。

## F5c 特征历史操作

新增 `cad_history` 与终端 `HISTORY` 的抑制、恢复、完整合法重排、回滚预览及返回末端。AI 请求要求作者身份及实际游标对象，末端明确传 null。回滚不改变作者 StateId，所以游标在排队回调的会话锁内单独校验；抑制还要求真实结果 revision。所有修改复用现有生产命令，输入顺序和后继不能被重新猜测。AI 的字符串 HISTORY 入口要求改用 `cad_history`，避免略过显式游标身份。

最终 164 命令/AI/参数/历史联合检查通过，包含合法/非法依赖顺序、实际 Boolean 抑制恢复、旧状态/旧游标/取消的排队守卫，以及作者状态和临时输出分离。Release 0 警告/错误，冻结发布 `artifacts/publish/f5-history-20261003-2` 的桌面/C1/E1 三窗口通过，绑定日志均为空；实际终端控件检查包含回滚、返回末端和抑制恢复。四张最终截图已逐张检查。

1788 全量在最终游标 JSON 小写字段修复前通过，不能声明它覆盖最终 ViewModels 程序集；最终 164 专项明确覆盖复制实际返回 cursor 后调用 return_end。CommandLine/Commands/Editor 与全量冻结一致，ViewModels 的哈希变化及完整边界记录于 `artifacts/f5-history-verification-1.json`。第一次桌面辅助函数未等候按钮绑定刷新而失败的证据保留，补 dispatcher 等候后的最终检查通过。

原生文档 schema 和锁定 `curve-inspection.8` SDK 不变。本轮没有请求模型供应商；工具调用、终端控件、实际模型长会话与物理指针分别记录。[F5d关联基准作者入口](F5_ASSOCIATED_DATUM_TOOLS.md)已实现并通过154联合专项，1827全量及六窗口通过，冻结证据见artifacts/f5-datums-verification-1.json；通用配方编辑、常用建模动作、更多工程图/文件入口及 A/B/C/D/E/F 余项继续，不能将本页当作全部 CAD 完成记录。后续契约见 [F5 计划](F5_COMMAND_AND_AGENT_PLAN.md)。
