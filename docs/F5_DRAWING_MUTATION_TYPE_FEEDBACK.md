# F5：工程图修改动作的对象类型反馈

2026-10-13，开发源码1079，使用公开OcctSharp Preview.85，无新SDK API。

1077实际工程图失败中，模型在已经创建Top视图后试图将建模feature ID交给`cad_drawing delete`。缺type时只得到“Missing drawing object type”，填type=feature后反而得到“Exact sheetId owner is required”，然后继续添加错误owner/path。错误类型本应先被明确拒绝；本次修正这条生产反馈路径，不据此证明全部模型失败的根因。

`create/edit/reselect/delete`现在在原字段、精确document/state、关闭/分离与只读守卫之后，先验证动作的既有对象类型。错误或缺失type返回success=false、invalid_drawing_object_type、missingOrInvalid和固定allowedTypes/allowedArguments及只读contract查询；不查询或填入owner/id，不把模型特征自动转换为工程图对象。

类型来自同一份既有图纸对象目录。delete允许十类图纸/库对象；create/edit不允许直接修改库模板，仍须save_template/import_template；reselect仅允许原生产附着契约的view、dimension、datum、control_frame、balloon、hole_table。sheet/field/note没有来源附着，不再被列为reselect可选类型。parts_table/zones继续走专用动作。inspect仍返回原十类类型与原顺序，工具schema未增加。

[独立证据](../capability-evidence/diagnostics/af1079-drawing-object-type/verification.json)：原1190输入只改两份生产源码，新增101项检查，原6664测试源码保持。原1078程序集每配置30通过/71复现反馈失败；最终隔离候选及合入后根各158/158通过（原57加新101）。检查覆盖四动作/八类错误/空及有图纸、外来文档/状态、未知字段、取消/分离、合法类型仍要求精确owner/id、显式只读查询后创建/删除及撤销重做。类型反馈不触发几何/投影，资产归零。

首轮新夹具每配置122通过/33失败：实际空图纸创建回执为Drawing kernel unavailable。修正测试内核以实现生产要求的ITechnicalDrawingKernel能力接口，接口仍在投影被请求时抛错，并保留零几何/投影调用断言；第二轮每配置155通过。原失败、真实诊断和最终更精确动作类型的158检查分别保存，不把夹具失败称为产品修复，也没有放宽原断言。

Tests依赖图和WPF两配置build0，四份输出SDK13/native62磁盘匹配公开85。探针禁止原生加载，实际零原生模块；raw/wire工程图schema和原8192、1024输出、安全余量、十二工具检查保持。这是编译断言直接调用的托管证据，不是完整xunit/原生全量。

预计完整6765尚未发现/运行，当前原模型未复验。最终性能、D6/材质/图形故障窗口须等待SDK交还原生时段，并绑定最新源码/载荷；原STEP24和A–F其他范围继续，G与清理暂缓。
