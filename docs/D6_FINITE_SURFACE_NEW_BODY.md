# D6 有限曲面新实体与自由起始平面

2026-10-13 当前代码已合入生产1076，真实36窗口仍未通过。[1077只读观察器4](../capability-evidence/diagnostics/af1077-window-observer/verification.json)两配置构建零警告/错误，19生产程序集、worker三文件、SDK13/native62及语言资源逐字节匹配最终WPF载荷。原29处Require断言、六状态、36个精确夹具和30/300秒均保持，只增加真实owner/modal窗口身份、焦点、打开的选择器及起始XYZ诊断。上一轮只发现owner、菜单失焦并发生Y减量，最终取消且轨迹未完成；新观察器尚未桌面运行。输入必须来自Computer Use实际返回并重新观察的目标窗口，诊断HWND不能冒充工具窗口对象；只发现禁用owner时停止输入并保留现场。当前Debug及两模型场景结束后先交还SDK原生验证时段，不抢占执行此矩阵。以下1073/SDK31叙述为历史检查点。

2026-10-12，status225补充：[修订5窗口观察器](../capability-evidence/AF_PENDING_D6_WINDOW_OBSERVER_V3_BUILD_VERIFICATION.json)引用最终1073/SDK31，两配置编译0警告/错误并核验磁盘载荷，实际窗口尚未运行。创建/编辑双阶段三语言×六几何共36矩阵保持；创建版缺`SketchDraft`命名空间的失败、修订4同步开窗源码及原队列均保留。修订5先排入开窗请求，观察器在`ShowDialog()`模态循环内运行，关闭后等待生产请求完成，原六状态及30/300秒限制不变。[SDK30组合候选](D6_SDK30_PRODUCTION_INTEGRATION.md)source1066两配置构建0、原6467+新63发现6530，实际各99/99和165/165及native62已验；完整6530和后续solver21原6556/最终D6完整6619按新独立队列顺序执行，不替代其他A–F范围或生产合入。

2026-10-12，status224。隔离 source1073 / SDK31 / solver21 已完成新实体实现和两配置专项验证；完整回归、真实窗口及生产合入尚未完成。正式已验子批仍为 D5；A–F 继续，G 与清理暂缓。

## 作者与几何契约

`FiniteSurface + NewBody` 接受自由指定的刚体起始平面 frame、完整轮廓、孔及递归岛。作者不携带 Source、SupportFace 或 targetBody，必须明确保存有限终止面的文档、特征、资产、修订、指纹及 Near/Far 分支。终止面仍限平面、球面、柱面；“自由起始”指平面 frame，不包括自由曲面。

现有公开 Until 构造器需要源实体及成员支撑面。实现仅复用已封装 API，在起始平面后方建立私有精确包围盒作为支撑，调用原 Until 加料及 D5 完整材料证明。原生构造成功、完整起始面积、定向棱柱、所有终止盖面与原有限面域的精确 Common 覆盖及分支证明全部通过后，才把已证明的 delta 保存为独立实体。原生失败或任何证明失败都整次拒绝，没有备用 Boolean 构造路径。

单 solid 或由完整、不重复、不干涉 solid 构成的 compound 可以作为独立结果；额外松散拓扑拒绝。私有 seed 资产在成功、拒绝、晚到取消、关闭及 generation 变化后释放，不进入作者、特征输入或持久化数据。临时选择身份只用于原内部支撑证明，不冒充用户选择或修改终止面的原始身份。

源修订改变后冻结旧结果，必须由调用方明确重选当前终止面。保存重开执行完整作者重放，并比较结果的精确 Common 体积与拓扑，拒绝同体积异地结果。参数重算、独立零件复制、工程图关联及结构化 `cad_model` 已纳入专项；编辑器禁用新实体支撑面选择，加载不自动选择终止面。

持久化字段和 document60 / features43 section 版本保持。只有实际含新实体作者的文件声明 `cadoryx.finite-surface-new-body.1`，creator 为 `0.7.11`；缺少能力声明的新实体文件拒绝加载，其他文件原版本行为保持。没有增加原生封装，没有修改公共 OcctSharp 或生成输出。

## 当前证据

| 检查 | 实际结果 | 证据 |
|---|---|---|
| 原生前提 | 真实 Release/Debug toolkit，共12正例及4过小有限面拒绝；输入BRep字节保持 | [前提审计](../capability-evidence/AF_PENDING_D6_NEWBODY_NATIVE_PREREQUISITE.json) |
| 完整solution构建与发现 | Release/Debug均0警告/错误，原6556及新增63共6619保持 | [构建审计](../artifacts/d6-finite-newbody-draft-4/build-both-1-verification.json) |
| 最终有限曲面专项 | Release/Debug各99/99，含原36及新增63；实际SDK31/native62 | [汇总审计](../capability-evidence/AF_PENDING_D6_FINITE_NEWBODY_FUNCTIONAL.json) |
| 最终AI/工程图专项 | Release/Debug各189/189，原名称、断言、预算保持 | 同上 |
| 恢复补丁 | 11来源文件；独立应用并逐一复核全部1073哈希 | [恢复说明](../capability-evidence/AF_PENDING_D6_FINITE_NEWBODY_RECOVERY.json)、[可读差异](../capability-evidence/AF_PENDING_D6_FINITE_NEWBODY_REVIEW.diff)、[字节补丁](../capability-evidence/AF_PENDING_D6_FINITE_NEWBODY.patch) |
| 实际窗口样本准备 | 六份平面/球面/柱面×正反向文件已生成、保存、重开；section全哈希、creator/能力及native62独立通过，资产归零 | [夹具审计](../capability-evidence/AF_PENDING_D6_WINDOW_FIXTURES_VERIFICATION.json) |
| 完整6619两配置 | 原协调进程已消失且无终态；新队列在SDK30完整6530与solver21原6556之后执行，尚未运行 | [续接检查点](../capability-evidence/AF_PENDING_STATUS225_CONTINUATION_CHECKPOINT.json) |
| 实际WPF窗口、提供方及性能 | NOT RUN | 不以ViewModel或专项代替 |

应用 Debug 使用私有包内 Release native。前提探针另有真正 Debug toolkit 的证据，两者分别记录。63个新案例包括生产 ViewModel，但不证明实际WPF窗口。

## F5 联合回归发现与修复

原source1070及D6第二草案的189专项两配置各188通过/1失败：短恢复场景激进模式只保留11工具。实际工具预算方法确认成本5606超过原可用5597；仅精简两处工程图schema重复描述后降至5596，普通/激进均保留12工具，最终两配置189全部通过。

完整 `occurrences.items[].path` 指向、执行错误引导及全部身份守卫保持；选择算法、8192上下文、1024输出、640/1280安全区、6000/2400结果及16工具轮不变。激进短恢复场景仅余1 token，不保证任意长会话都保留12工具；原Bonsai仍需按最终载荷复验。原四份联合失败记录及实际5606/5596预算输出见汇总审计。

前两轮原生探针共48份失败、两次新测试编译错误、诊断反射构造失败均保留。首次汇总审计直接比较原绝对feed与已规范NuGet.Config而停止；只修审计以使用既有V2规范哈希，未修改候选或重新运行通过的矩阵。

## 接下来

收齐最终6619两配置及独立运行载荷审计，再验证实际编辑器选择、预览、确认/取消、树编辑及保存重开。生产仍 source1000 / SDK26，当前补丁未应用；恢复时先按solver21恢复链构造source1070/SDK31，再应用本11文件补丁。不能直接把本补丁当作SDK26生产增量。

窗口验收按创建/编辑两个阶段、六份样本在中/英/日各运行，共36场景，各样本覆盖确认/取消/关闭；先核对最终WPF/应用/SDK载荷与冻结候选，再在本任务创建的窗口打开样本。通过真实创建命令、树编辑及方向/起始平面/终止类型控件验证新实体不要求目标或支撑面；球面创建经真实profile控件选择准备好的圆草图。加载面清空选择，缺少明确终止面时拒绝预览，明确重选Near后才允许预览/确认。改变Far使候选失效，不能把Near结果当Far；取消和关窗保持作者与历史。最后核验确认、撤销/重做、保存/重开及独立原生读回。以computer-use实际窗口观察和输入记录验收；渲染截图、ViewModel专项或文件夹具不替代真实控件证据。开始前提示用户，不能关闭其他窗口或停止原有队列。

夹具首次编译的构造参数错误仅修为具名 `surfaceExtrusionResolver`，原失败来源及日志保留；成功轮同时按字段比较重开作者，避免把集合的对象引用相等当持久化相等。夹具与恢复源码在本地保存，没有控制桌面或清理记录。

任意终止曲面、自由曲面起始、所有Far几何、完整子拓扑历史/逐面样式仍未完成。B18原严格STEP导出24拒绝、外部原版OCCT端盖误差、A5窗口/材质/性能、C/E生产组合、制造交换及提供方范围继续，不放宽公差、身份或验收条件。
