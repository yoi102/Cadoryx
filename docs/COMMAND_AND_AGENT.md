# 命令行与 AI Agent

2026-09-27 完善交互。结构沿用 Direct2dCad 的独立命令解析、Agent 对话协议、LM Studio/Codex 连接及停靠 Toolbox 模式；CAD 执行端使用 Cadoryx 的文档会话与三维几何命令。

## 入口与操作

底部“命令” Toolbox 可用 `Ctrl+反引号` 打开。输入框支持 Enter 执行、上/下方向键查历史；输入命令名前缀时出现可选择的补全，方向键选择、Tab 或 Enter 接受、鼠标点击接受。输出列表自动跟随最新结果。输入的命令逐条显示输入与结果/错误；`CLEAR` 按定义清空显示记录。按钮、视口或 AI 工具产生的文档编辑在**实际提交后**显示命令名，撤销/重做显示所针对的命令；失败的编辑不会显示为成功。工作区保存、导出、标准视图切换、适应窗口和建模工具启动也显示操作行。警告与错误会显示在命令面板中。`HELP` 列出命令，`HELP BOX` 查看单个命令。当前命令集：

| 命令 | 行为 |
|---|---|
| `STATUS`、`LIST` | 查看当前文档、选择及最多 50 个实体 |
| `STATS` | 查看定义、实例、特征与几何资产大小 |
| `FIND name`、`SELECTION` | 按名称查找实体实例、列出当前选中实体 |
| `PARTS`、`FEATURES`、`LAYERS`、`MATERIALS` | 列出零件、特征、图层、材质及其 ID 和简要状态 |
| `SKETCHES`、`DRAWINGS`、`OCCURRENCES` | 列出草图、图纸和装配实例；各列表最多 50 项 |
| `SELECT exact-name` | 选择名称唯一的实体实例；装配中重名时拒绝猜测 |
| `UNDO`、`REDO` | 通过活动文档会话撤销/重做 |
| `FIT`、`VIEW TOP/FRONT/RIGHT/BACK/LEFT/BOTTOM/ISO` | 使用现有视口导航和动画 |
| `DESELECT` | 清除当前实体选择 |
| `FOCUS`、`ISOLATE`、`HIDE`、`SHOWALL` | 聚焦、隔离、临时隐藏当前选择或恢复全部显示；无选择/无过滤时明确拒绝 |
| `DISPLAY [SHADED\|WIREFRAME]` | 查询或切换当前视口显示模式 |
| `GRID [ON\|OFF\|SPACING mm\|SNAP ON\|OFF]` | 查询或修改当前文档网格；格距限定 0.1–1000 mm，修改可撤销 |
| `TOOL BOX`、`TOOL CYLINDER`、`CANCEL` | 进入鼠标定尺寸的立体建模模式，或取消当前交互 |
| `BOX name width depth height [x y z]` | 在当前建模目标零件中创建长方体；尺寸与位置均为毫米 |
| `CYLINDER name radius height [x y z]` | 创建圆柱；尺寸与位置均为毫米 |
| `CLEAR` | 清除命令面板的显示记录 |

含空格的名称用双引号，例如 `BOX "Bracket 1" 40 20 10 0 0 5`。输入数字使用小数点。建模命令走 `AddBodyCommand` 与文档历史，遵守只读文档、目标零件、图层锁定和内核校验；多零件文档未选目标时会拒绝创建，不会猜测第一个零件。网格修改同样走文档会话；隔离/隐藏调用现有审阅视图的临时显示命令，属于视图状态而非 CAD 文件编辑。命令模块仅管理解析、注册和语法，具体 CAD 行为由 `CadCommandContext` 接到当前文档。输出是当前会话的有界显示记录，最多保留 500 行，不写入 CAD 文件；鼠标悬停、选择移动等高频交互不会逐条打印。尚未提供可持久化、可回放的完整操作审计，也未接入需要文件路径/对话框的 `SAVE`、`EXPORT` 命令或复杂建模全部参数。

右侧“AI 助手” Toolbox 默认收起，可用 `Ctrl+Shift+A` 打开。其布局参照 Direct2dCad：顶部连接状态、设置/清空入口，中部按角色区分的消息卡片，底部附件、输入、发送与停止。设置图标打开独立的 MetroWindow，可选择 LM Studio 或 Codex，填写本地模型地址/名称或 Codex 程序路径/模型，保存后发送。设置窗口内修改提供方或模型属于草稿；取消会恢复原值且保留对话，保存并切换连接后才开始新对话。Codex 推理强度为下拉选项：`none`、`minimal`、`low`、`medium`、`high`、`xhigh`，默认 `medium`。LM Studio 默认端点是 `http://localhost:1234/v1`；Codex 使用本机 `codex app-server`，需有可运行的 Codex CLI 与相应登录状态。Enter 发送，Shift+Enter 换行；消息自动滚动，工作中显示进度与停止按钮。支持一次最多五个 PNG/JPEG 图片（每个不超过 8 MB）或 UTF-8 文本/Markdown/CSV/JSON（每个不超过 1 MB）附件，可用回形针按钮添加；输入框内 Ctrl+V 也可粘贴图片或文件，普通文本粘贴保持原样。文件内容随请求发给选定提供方。缺少 LM Studio 模型时，发送会保留输入和附件，提示先选模型。两个提供方的对话都支持取消与清空；切换文档时清空会话及待发送附件，避免将上一文档上下文带到新文档。设置保存在用户本地应用数据目录，不写入 `.cadoryx` 文件。

AI 工具目录现有五个入口：`cad_status`、`cad_command`、`cad_inspect`、`cad_select`、`cad_edit`。`cad_status` 返回活动文档的 `documentId`、`stateId`、实体/零件/特征/草图/图纸/选择数量，以及从命令注册表动态生成的语法清单；无活动文档时明确返回 `active: false`。`cad_command` 只接受已注册的命令，其文本结果适合直接查看或导航。需要精确身份和后续编辑时，使用 `cad_inspect` 的结构化结果：

| `cad_inspect.area` | 返回内容 |
|---|---|
| `document` | 文档单位、网格、工作平面和各类对象计数 |
| `parts`、`bodies`、`features` | 零件 ID、实体所属零件/特征、几何类别与包围盒、特征配方/依赖/抑制状态 |
| `layers`、`materials` | 精确 ID、可见/锁定状态、颜色或密度 |
| `selection` | 当前选择的实例完整路径、实体 ID、几何修订及是否仍与当前文档一致 |
| `sketches` | 所属零件、修订及曲线/约束数量 |
| `occurrences`、`assembly` | 实例路径、定义 ID、关系目标和诊断；`assembly` 保留 `items.instances` 与 `items.constraints` 两组 |
| `drawings` | 图纸摘要、视图、关联来源数量及前 50 个尺寸状态 |
| `drawing_views` | 指定 `sheetId` 下视图分页或按 `id` 精确查询；包含父视图、比例、位置、失效原因与来源实例/实体/修订/资源身份，不返回投影笔画缓存 |
| `drawing_dimensions` | 指定 `sheetId` 下尺寸分页或按 `id` 精确查询；包含视图、数值、公差、失效原因与两侧几何基准身份 |

列表默认最多 50 项；可用 `offset`（0–100000）和 `limit`（1–50）分页，返回 `total`、`hasMore`、当前 `documentId/stateId`。`assembly` 因有两组列表，分别返回 `instanceTotal`、`total`（关系数）和两个 `hasMore` 标记。`id` 按对象精确 GUID 过滤，`partId` 可缩小实体、特征和草图结果。每次翻页都应核对 `stateId`；文档变动后从头查询。`drawing_views` 与 `drawing_dimensions` 必须传从 `drawings` 得到的精确 `sheetId`，不存在的图纸会明确报错。图纸摘要内嵌全部视图（模型上限为每张 128 个）和前 50 个尺寸；尺寸模型上限为每张 10,000 个，余下尺寸通过 `drawing_dimensions` 分页取得。

`cad_select` 解决重名装配实例的选择问题。先从 `cad_inspect.occurrences` 取得完整 `path`，从 `cad_inspect.bodies` 取得 `bodyId` 与几何 `revision`；调用时还要传当前 `documentId/stateId`。仅传 `path` 可选中实例；同时传 `path/bodyId/revision` 可选中该实例下的一个实体。路径、所属零件或几何修订不匹配时拒绝改变选择。选择是当前视图状态，不写入文档历史；之后可通过 `cad_command` 调用 `FOCUS`、`HIDE`、`ISOLATE` 等操作。按名称的 `SELECT` 仍只在名称唯一时成功。`cad_edit` 只支持下表中的结构化动作，不执行任意程序集 API：

| 动作 | 必须提供的精确输入 | 结果 |
|---|---|---|
| `create_box`、`create_cylinder` | `name`、Box 的 `width/depth/height` 或 Cylinder 的 `radius/height`；有零件时必须传精确 `partId`，可选 `layerId/materialId` 与 `x/y/z`，单位毫米 | 经内核验证创建形体，返回 `bodyId/featureId/partId`；空文档可自动建立首个零件 |
| `create_sketch_rectangle` | `partId`、`name`、`x/y/width/height`（毫米） | 在指定零件的局部 XY 平面创建矩形草图并求解；返回草图 ID |
| `rename_sketch` | 草图 `id`、`revision`、`name` | 经草图求解和关联重算改名 |
| `fix_assembly_instance` | 实例完整 `path`、`name` | 建立固定关系；返回关系 ID |
| `set_assembly_constraint_enabled`、`set_assembly_distance` | 关系 `id` 与 `enabled` 或 `distanceMm` | 修改指定关系；距离仅适用于距离关系 |
| `create_drawing_sheet`、`rename_drawing_sheet` | `name`；重命名另需图纸 `id` | 创建 A4 横向图纸或改名 |
| `add_drawing_view` | `sheetId`、来源实例完整 `path`、`kind`（Front/Top/Right/Isometric）、`name/scale/centerX/centerY` | 投影指定实例并添加关联视图；返回视图 ID |

每个结构化编辑都必须带最近一次查询返回的 `documentId` 和 `stateId`；文档切换或状态变化后拒绝旧请求，须重新查询。形体尺寸限定为正数且不超过 1,000,000 mm，位移绝对值不超过 1,000,000 mm；零件、图层和材料交由会话命令校验，不允许猜测目标零件。实例路径必须与当前文档中的完整路径一致。草图编辑还校验修订。编辑均通过文档会话提交，有撤销记录；内核、求解、投影或验证失败不会发布半成品。`cad_command` 的命令行编辑遵循活动文档自身校验，但不具有结构化编辑的 `stateId` 前置条件；需要精确目标时优先用 `cad_edit`。尚未让 Agent 自行拾取 BRep 面/边或猜测装配基准，也没有向工具开放任意曲线、孔岛、剖切视图和关联尺寸的创建。`SELECT` 只有实体实例名称唯一时才会改变选择。关闭“允许 CAD 工具”后只进行普通对话。Codex app-server 线程使用只读沙盒，CAD 修改通过受限工具和文档会话。AI 回复本身不构成建模成功证据，以工具返回和文档实际状态为准。

## 验证与剩余边界

`Cadoryx.Tests/CommandLineTests.cs` 覆盖语法拒绝、活动文档路由与输出；`AiAssistantTests.cs` 覆盖设置与取消、工具调用轮次、Toolbox 附件到请求对象、LM Studio HTTP 请求中的文本/图片附件和工具 schema。`CadAgentDocumentToolTests.cs` 使用真实文档会话覆盖草图创建/改名、固定装配关系、状态过期拒绝、工程图图纸/投影视图及撤销。2026-09-28 的 `scripts/verify.ps1 -PublishSmoke` 通过锁定还原、Release 零警告构建、592/592 自动测试、CLI 与发布版桌面冒烟，证据 `artifacts/smoke-20260928-003717`，绑定日志为空；桌面冒烟未直接操控外部服务或文件对话框。

2026-09-29 补充命令面板输出验收：Release 构建零警告，604/604 自动测试通过；定向测试覆盖输入/结果/错误、实际提交、失败编辑不产生活动行及撤销/重做命令名。发布版 `--smoke` 验证了按钮触发的适应窗口会显示 `Action` 行，`artifacts/command-output-smoke/command-agent-result.txt` 为 PASS，绑定日志为空。真实键盘输入和更长操作链仍需人工检查。

随后扩充到 30 条注册命令。Release 构建零警告、605/605 自动测试通过；定向测试覆盖新命令的参数拒绝、网格/显示/可见性路由。发布版桌面冒烟在真实活动文档中验证 `LAYERS` 查询、`GRID SPACING` 修改及 `UNDO`、`DISPLAY WIREFRAME/SHADED`、`HIDE/SHOWALL`，`artifacts/expanded-command-smoke-verified/command-agent-result.txt` 与整套 `result.txt` 均为 PASS，绑定日志为空。`HELP` 与 `cad_status.commands` 动态返回当前命令目录；AI 工具描述不再保留容易过期的固定命令清单。

随后仅将混合的命令/Agent 测试按职责拆为 `CommandLineTests.cs`（4 项）和 `AiAssistantTests.cs`（8 项）；测试断言未改。拆分后 Release 构建零警告、两组定向测试 12/12 通过，完整 605 项未因纯文件整理重跑。

2026-09-29 结构化信息与建模扩充后，`CadAgentDocumentToolTests` 新增真实文档会话用例，验证目录 schema、分页、精确筛选、选择、Box/Cylinder 的状态过期、错误参数、错误零件以及两步撤销。完整覆盖率运行 615/615 通过，原始 Cobertura 和模块统计见[自动测试与覆盖率](TEST_COVERAGE.md)。本次没有重新执行带真实 AI 提供方的端到端聊天或桌面交互，协议与工具行为由离线自动测试验证。

随后新增 `cad_select` 的重名实例定向测试及默认 Agent 上下文工具保留测试。完整覆盖率运行 617/617 通过；精确选择、错误文档/状态/实体/修订拒绝均由真实文档会话测试覆盖，见[自动测试与覆盖率](TEST_COVERAGE.md)。真实提供方端到端聊天与桌面交互仍未在此轮复测。

随后补充 `drawing_views` 和 `drawing_dimensions` 图纸内部查询，保留原有五个工具入口。真实文档会话测试使用 51 个尺寸验证摘要之外的分页、精确 ID 筛选、错误图纸 ID 拒绝、状态标识及来源/基准身份。验证结果见[自动测试与覆盖率](TEST_COVERAGE.md)；这些测试不代表真实模型调用或图纸窗口交互验收。

本机可复现连接检查：`dotnet run --project tools/AgentConnectionProbe/AgentConnectionProbe.csproj -c Release -- --provider codex --executable <codex.cmd 路径>`，加 `--model <模型> --message <提示>` 可验证对话，加 `--file <txt/png 路径>` 可验证附件，加 `--tool-roundtrip` 可验证动态工具调用；LM Studio 改为 `--provider lmstudio --endpoint <实际服务地址>`，加 `--agent-runner` 可走 UI 使用的 Agent 对话流程。探针只读取模型/发送指定提示与附件，不访问 CAD 文档。2026-09-28 本机 Codex app-server 成功列出 4 个模型，`gpt-5.6-luna` 返回普通对话 `OK`、文本附件校验词 `COBALT42`，从有效的 16×16 红色 PNG 回答 `Red`，并调用 `probe_echo` 返回其工具结果。LM Studio 实际运行在 `http://localhost:15630/v1`（不是设置中的默认 1234 端口）；`prism-ml/bonsai-27b` 的模型发现、普通对话 `OK`、文本附件 `COBALT42`、图片附件 `Red` 均通过，图片也通过 `--agent-runner` 路径。一次无效 PNG 曾使 Codex 回复 `NO_IMAGE`，换成经图像解码检查的有效文件后通过；LM Studio 图片曾在 25 秒内超时、另一次返回空文本，延长探针上限并复测成功，说明本机视觉推理耗时与输出稳定性还需长时间验收。文件对话框及 Ctrl+V 的人工操作、长对话、多 DPI 与其他模型响应质量仍需单独验收。
