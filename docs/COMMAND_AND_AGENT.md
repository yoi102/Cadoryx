# 命令行与 AI Agent

2026-09-27 完善交互。结构沿用 Direct2dCad 的独立命令解析、Agent 对话协议、LM Studio/Codex 连接及停靠 Toolbox 模式；CAD 执行端使用 Cadoryx 的文档会话与三维几何命令。

## 入口与操作

底部“命令” Toolbox 可用 `Ctrl+反引号` 打开。输入框支持 Enter 执行、上/下方向键查历史；输入命令名前缀时出现可选择的补全，方向键选择、Tab 或 Enter 接受、鼠标点击接受。输出列表自动跟随最新结果。`HELP` 列出命令，`HELP BOX` 查看单个命令。当前命令集：

| 命令 | 行为 |
|---|---|
| `STATUS`、`LIST` | 查看当前文档、选择及最多 50 个实体 |
| `FIND name`、`SELECTION` | 按名称查找实体实例、列出当前选中实体 |
| `SELECT exact-name` | 选择名称唯一的实体实例；装配中重名时拒绝猜测 |
| `UNDO`、`REDO` | 通过活动文档会话撤销/重做 |
| `FIT`、`VIEW TOP/FRONT/RIGHT/BACK/LEFT/BOTTOM/ISO` | 使用现有视口导航和动画 |
| `DESELECT` | 清除当前实体选择 |
| `TOOL BOX`、`TOOL CYLINDER`、`CANCEL` | 进入鼠标定尺寸的立体建模模式，或取消当前交互 |
| `BOX name width depth height [x y z]` | 在当前建模目标零件中创建长方体；尺寸与位置均为毫米 |
| `CYLINDER name radius height [x y z]` | 创建圆柱；尺寸与位置均为毫米 |
| `CLEAR` | 清除命令面板的显示记录 |

含空格的名称用双引号，例如 `BOX "Bracket 1" 40 20 10 0 0 5`。输入数字使用小数点。建模命令走 `AddBodyCommand` 与文档历史，遵守只读文档、目标零件、图层锁定和内核校验；多零件文档未选目标时会拒绝创建，不会猜测第一个零件。命令模块仅管理解析、注册和语法，具体 CAD 行为由 `CadCommandContext` 接到当前文档。

右侧“AI 助手” Toolbox 默认收起，可用 `Ctrl+Shift+A` 打开。其布局参照 Direct2dCad：顶部连接状态、设置/清空入口，中部按角色区分的消息卡片，底部附件、输入、发送与停止。设置图标打开独立的 MetroWindow，可选择 LM Studio 或 Codex，填写本地模型地址/名称或 Codex 程序路径/模型，保存后发送。设置窗口内修改提供方或模型属于草稿；取消会恢复原值且保留对话，保存并切换连接后才开始新对话。Codex 推理强度为下拉选项：`none`、`minimal`、`low`、`medium`、`high`、`xhigh`，默认 `medium`。LM Studio 默认端点是 `http://localhost:1234/v1`；Codex 使用本机 `codex app-server`，需有可运行的 Codex CLI 与相应登录状态。Enter 发送，Shift+Enter 换行；消息自动滚动，工作中显示进度与停止按钮。支持一次最多五个 PNG/JPEG 图片（每个不超过 8 MB）或 UTF-8 文本/Markdown/CSV/JSON（每个不超过 1 MB）附件，可用回形针按钮添加；输入框内 Ctrl+V 也可粘贴图片或文件，普通文本粘贴保持原样。文件内容随请求发给选定提供方。缺少 LM Studio 模型时，发送会保留输入和附件，提示先选模型。两个提供方的对话都支持取消与清空；切换文档时清空会话及待发送附件，避免将上一文档上下文带到新文档。设置保存在用户本地应用数据目录，不写入 `.cadoryx` 文件。

AI 工具目录现为 `cad_status`、`cad_command`、`cad_inspect`、`cad_edit`。`cad_command` 只接受已注册的上述命令；`cad_inspect` 按 `sketches`、`assembly`、`drawings` 返回有界列表与精确文档/状态 ID，可用 `id` 过滤。草图返回所属零件、修订与几何/约束数量；装配返回实例路径、关系目标及当前诊断；工程图返回图纸、视图、关联来源数量和尺寸状态。`cad_edit` 只支持下表中的结构化动作，不执行任意程序集 API：

| 动作 | 必须提供的精确输入 | 结果 |
|---|---|---|
| `create_sketch_rectangle` | `partId`、`name`、`x/y/width/height`（毫米） | 在指定零件的局部 XY 平面创建矩形草图并求解；返回草图 ID |
| `rename_sketch` | 草图 `id`、`revision`、`name` | 经草图求解和关联重算改名 |
| `fix_assembly_instance` | 实例完整 `path`、`name` | 建立固定关系；返回关系 ID |
| `set_assembly_constraint_enabled`、`set_assembly_distance` | 关系 `id` 与 `enabled` 或 `distanceMm` | 修改指定关系；距离仅适用于距离关系 |
| `create_drawing_sheet`、`rename_drawing_sheet` | `name`；重命名另需图纸 `id` | 创建 A4 横向图纸或改名 |
| `add_drawing_view` | `sheetId`、来源实例完整 `path`、`kind`（Front/Top/Right/Isometric）、`name/scale/centerX/centerY` | 投影指定实例并添加关联视图；返回视图 ID |

每个编辑都必须带最近一次查询返回的 `documentId` 和 `stateId`；文档切换或状态变化后拒绝旧请求，须重新查询。实例路径必须与当前文档中的完整路径一致。草图编辑还校验修订。编辑均通过文档会话提交，有撤销记录；求解、投影或验证失败不会发布半成品。尚未让 Agent 自行拾取 BRep 面/边或猜测装配基准，也没有向工具开放任意曲线、孔岛、剖切视图和关联尺寸的创建。`SELECT` 只有实体实例名称唯一时才会改变选择。关闭“允许 CAD 工具”后只进行普通对话。Codex app-server 线程使用只读沙盒，CAD 修改通过受限工具和文档会话。AI 回复本身不构成建模成功证据，以工具返回和文档实际状态为准。

## 验证与剩余边界

`Cadoryx.Tests/CommandAndAgentTests.cs` 覆盖语法拒绝、活动文档路由、设置与取消、工具调用轮次、Toolbox 附件到请求对象、LM Studio HTTP 请求中的文本/图片附件和工具 schema。`CadAgentDocumentToolTests.cs` 使用真实文档会话覆盖草图创建/改名、固定装配关系、状态过期拒绝、工程图图纸/投影视图及撤销。2026-09-28 的 `scripts/verify.ps1 -PublishSmoke` 通过锁定还原、Release 零警告构建、592/592 自动测试、CLI 与发布版桌面冒烟，证据 `artifacts/smoke-20260928-003717`，绑定日志为空；桌面冒烟未直接操控外部服务或文件对话框。

本机可复现连接检查：`dotnet run --project tools/AgentConnectionProbe/AgentConnectionProbe.csproj -c Release -- --provider codex --executable <codex.cmd 路径>`，加 `--model <模型> --message <提示>` 可验证对话，加 `--file <txt/png 路径>` 可验证附件，加 `--tool-roundtrip` 可验证动态工具调用；LM Studio 改为 `--provider lmstudio --endpoint <实际服务地址>`，加 `--agent-runner` 可走 UI 使用的 Agent 对话流程。探针只读取模型/发送指定提示与附件，不访问 CAD 文档。2026-09-28 本机 Codex app-server 成功列出 4 个模型，`gpt-5.6-luna` 返回普通对话 `OK`、文本附件校验词 `COBALT42`，从有效的 16×16 红色 PNG 回答 `Red`，并调用 `probe_echo` 返回其工具结果。LM Studio 实际运行在 `http://localhost:15630/v1`（不是设置中的默认 1234 端口）；`prism-ml/bonsai-27b` 的模型发现、普通对话 `OK`、文本附件 `COBALT42`、图片附件 `Red` 均通过，图片也通过 `--agent-runner` 路径。一次无效 PNG 曾使 Codex 回复 `NO_IMAGE`，换成经图像解码检查的有效文件后通过；LM Studio 图片曾在 25 秒内超时、另一次返回空文本，延长探针上限并复测成功，说明本机视觉推理耗时与输出稳定性还需长时间验收。文件对话框及 Ctrl+V 的人工操作、长对话、多 DPI 与其他模型响应质量仍需单独验收。
