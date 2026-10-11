# F5f 文件输出与真实供应商验证

更新：2026-10-04。用户授权完成 A–F，第七批实机、安装及交付暂缓。本页记录 F5f 中已接入的文件输出和真实模型调用；[多文档作者入口](F5_WORKSPACE_TOOLS.md)已补齐本地契约，完整工程图工具仍继续，不将这部分当作 F5f 或整个 A–F 完成。

## 文件工具

第十个工具 `cad_file`：`contract`、`save`、`export`、`report`。先读取 `contract` 获取选项；写出要求实际 `documentId/stateId`、绝对路径，`overwrite` 默认 false。`optionsJson` 是最多2048字符的严格JSON字符串；工具请求最多8192字符。终端沿用4096字符预算，`FILE`接受同一请求，身份由当前上下文捕获；`options`对象可转换为字符串，不能同时提供两种表示。

| action | 输出与选项 |
|---|---|
| `save` | `.cadoryx` 完整作者文档，包括历史；允许在回滚预览时保存，预览不是保存对象 |
| `export` | STEP/STP、IGES/IGS、STL；线性网格误差mm、角度rad、二进制STL、文档可见性；默认0.1 mm、0.5 rad、true、true |
| `report` | 明确 `output=Package/BomCsv/ReviewHtml/ExchangeAudit`；对应 ZIP/CSV/HTML/ZIP。格式开关、误差及文档可见性沿用生产服务；ExchangeAudit必须选择至少一种格式 |

```text
FILE CONTRACT
FILE {"action":"save","path":"C:/CAD/work/model.cadoryx"}
FILE {"action":"export","path":"C:/CAD/work/model.step","options":{"visibleOnly":true}}
FILE {"action":"report","path":"C:/CAD/work/audit.zip","options":{"output":"ExchangeAudit","includeStep":true}}
```

文件操作不新增撤销记录。`CadDocumentSession.WriteFileAsync`使用已有保存队列；在排队前和获得队列之后分别检查实际文档状态，并在会话锁内捕获作者快照及资产租约。导出和报告要求历史末端，避免把作者文档误称回滚预览。关闭令牌贯穿写出；队列中的旧请求在写出前拒绝。

捕获后允许其他合法文档编辑继续。结果明确返回写出快照的ID和state，`currentStateId`另列；保存期间发生编辑时仍保持dirty，不把较新的未保存状态标为已保存。所有输出先写同目录临时文件，再计算实际字节数和SHA256，在最终取消检查之后原子发布。已发布之后取消仍返回真实成功。预发布失败、原生写出结束后发生取消、错误诊断以及不允许的扩展名保留旧输出并清理临时文件。

回执给出实际路径、格式、字节数、哈希和捕获身份。工具输出最多内嵌八条诊断，每条消息最多400字符；`diagnosticCount/diagnosticsTruncated`明确说明省略边界，避免普通工具结果上限裁掉回执。生成成功不表示外部兼容性或制造标准符合性；交换实测依然使用[F4](F4_MEASURED_EXCHANGE.md)的观测边界。

## 供应商参数展示

真实LM Studio请求暴露两个此前离线检查未覆盖的问题：初始文件工具没有根`properties`导致HTTP 400；补根节点后，模型仍只看到建模工具根的文档身份，连续16轮遗漏`oneOf`中的action/authorJson。失败证据分别保留在`artifacts/f5-provider-lmstudio-1`和`-2`，均未通过。

`LmStudioChatClient`现在使用`ToolSchemaPresentation`，把局部`$defs/$ref`及`oneOf/anyOf/allOf`里的字段明确展开到供应商属性表，枚举列出全部合法候选、必填取分支交集、数量范围保留可接受的联合范围。供应商展示是字段联合，生产解析器继续检查每个动作的严格必填、禁止字段、精确身份、类型和几何契约；不把展示联合当作执行许可。外部、缺失及递归引用被拒绝。原始工具schema和原生文档schema不变，Codex路径仍使用原始工具schema。

默认8192-token短会话已保留全部十工具和最新请求。长期上下文中的工具筛选和历史压缩仍由AgentRunner按预算执行；供应商是否可见及实际是否调用使用单独模型证据，不以直接工具测试替代。

## 当前证据

- F5e冻结验证：170专项、1910全量、十窗口和八个生产DLL哈希一致，见[F5e](F5_COMMON_MODEL_TOOLS.md)。
- 首次文件冻结版本：179联合、1942全量、Release零警告/错误及桌面/C1/工程图三个窗口通过；发布`artifacts/publish/f5-files-20261004-1`。该版本不包含随后供应商schema修补，不能称当前源码的最终全量。
- 供应商修补后184联合专项通过，包含实际HTTP负载字段、完整作者schema展开、游标nullable和外部/递归schema拒绝；Release零警告/错误。最终同源码冻结发布`artifacts/publish/f5-files-20261004-2`全量1947/1947、三窗口PASS，十二个生产DLL与测试及实际模型探针一致，三份绑定日志为空。见artifacts/f5-files-verification-1.json。
- 实际终端TextBox绑定/Execute Automation Invoke已写出原生文档、STEP、BOM、HTML、Package及ExchangeAudit六类文件并核对实际SHA256与来源state；撤销/重做仍只作用原建模命令。两次冻结版`command-files.png`均已检查，选中行文字可读；长回执延续至视口下方，不声称完整输出都在截图内。
- `artifacts/f5-provider-lmstudio-3`实际LM Studio `http://localhost:15630/v1`、`prism-ml/bonsai-27b`十三轮PASS：生产拉伸8→12 mm³、实际编辑和undo/redo、八轮带数据附件的作者复查、实际保存及重开state确认。结束98条会话消息，资产归零；这是隔离候选构建，录制的是调用前结构化请求及返回，原始HTTP请求体在下一次同源码探针单列。
- `artifacts/f5-provider-lmstudio-4`使用普通Release生产程序集并录制48份实际HTTP请求体，十三轮PASS；模型8→12 mm³、undo/redo、附件复查与实际保存重开通过，资产归零。未修改用户文档/设置；探针只使用独立合成文档和其输出。默认1234端口读取超时，15630模型发现返回三个实际模型；没有替换不可用的已选供应商/模型。

- `artifacts/f5-provider-codex-1`实际Codex app-server、`gpt-5.6-luna`十三轮PASS：生产拉伸8→12 mm³、实际编辑、undo/redo、八轮数据附件复查和原生保存重开确认，资产归零。与LM Studio、测试及冻结发布使用相同十二生产DLL。原始结果中的1234 HTTP endpoint是探针未使用的默认参数，Codex路径通过codex.cmd启动app-server；验证汇总明确校正这一元数据，原始失败/结果不覆盖，后续探针源码已修正。

## 继续实施范围

[F5f多文档入口](F5_WORKSPACE_TOOLS.md)已接入新建、打开及精确激活；仍需接入完整工程图作者查询/修改/原始附着检查及PDF输出，两个已测供应商的进一步长期/取消/模型范围继续单列。随后仍回到[A–F跟踪](AF_IMPLEMENTATION_TRACKER.md)中的性能、复杂草图关系、面色/历史/螺纹、曲面终止/偏移、复杂拓扑、进一步制造和外部元数据余项。G第七批仍按用户要求暂缓。
