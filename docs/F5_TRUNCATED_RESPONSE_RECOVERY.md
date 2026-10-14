# F5：截断回复与完整工具批次

本批修复 LM Studio 回复完整性和有界恢复。工程图供应商工作流尚需按实际模型结果验收；不能把确定性测试通过视为 A–F 全部完成。原有严格 STEP 的精确 24 项继续按用户要求延期，不重试、不放宽断言，也不登记为通过。

原始 raw replay 第 19 个 HTTP 响应的 `finish_reason` 是 `length`，正文为空、工具列表为空。实际 usage 为 prompt 7691、completion 501、total 8192，因此只能断言达到一个 token 限制，不能声称耗尽请求的 1024 输出额度。之前已有 Top 视图的真实成功创建收据和只读检查；原始过程仍报错并结束，资产关闭后为零。这是本次复现的直接证据，不推断更早未录 raw 响应的失败根因。

客户端在解析工具调用之前拒绝整个截断回复，并抛出 `AiResponseTruncatedException`。即使其中第一个调用看起来完整，也不会执行。完整回复还须通过批次验证：所有调用的身份、函数、类型及参数对象必须有效，重复调用 ID、重复调用/函数属性和不完整批次均拒绝；参数对象内部重复属性和原始空白交给生产工具校验，省略或 null 参数保持原来的空对象兼容性。

AgentRunner 将截断与普通空回复共用原有一次恢复额度，在原工具轮数内继续。截断回复不加入会话、不生成工具收据，也不重放以前的编辑。下一条请求临时加入固定恢复指引，本次请求后续轮次维持已有保守上下文裁剪；模型、温度、8192 上下文、1024 输出、16 工具轮和原每轮四分钟保持。再次截断或轮数耗尽仍失败，取消立即生效。三种语言把恢复提示显示为 System 状态。

当前确定性证据：纯托管 40 项两配置均 40/40；保持同一 40 项测试载荷、只换回原客户端时为 8 通过/32 失败。32 是这套 transport 回归的问题复现数，与延期的 STEP24 无关。相关既有 AI/上下文/恢复测试及三语言提示两配置各 97/97；原测试名称保留。完整 solution Release/Debug 0 警告/错误。完整预计 7160 项尚未重跑。

[资格工具](../tools/CadAgentWorkflowProbeRawTransport/qualify-recovery.py)核对原 1200 输入的十个变化和新增测试，冻结五个变更模块及两个语言卫星程序集；其他核心、SDK、原生 62 DLL、worker 与既有 UI 候选字节保持。它仅证明本批差异载荷的来源和专项验证，不代替新的全量、桌面或性能验收。[原工作流录制投影](../tools/CadAgentWorkflowProbeRawTransport/generate.py)仅增加 HTTP 响应和异常录制，反向还原后与原三份工作流逐字节一致；提示、断言和预算没有修改。

D6 第二次实际窗口验收仍 FAIL：相关弹层截图点击未命中目标，返回的弹层 accessibility 索引不可用；临时 StartY 变为 -1，没有确认和原生预览，最终真实 Cancel 后原快照保持。不能将输入工具的问题归为已证明的几何缺陷，也不能通过原 36 窗口矩阵。记录与失败保留，后续须用实际有效目标完成输入路径。

原始运行、TRX、构建失败及不可变 qualification 在 `artifacts/f5-lmstudio-completion-integrity-1/` 保留。较早 parser-only qualification2 和 drawing1 保持原失败结果；后续运行使用新的 qualification 和 iteration，终态由[独立审计](../tools/CadAgentWorkflowProbeRawTransport/audit.py)核对。

实际终态：[drawing1](../capability-evidence/diagnostics/f5-lmstudio-completion-integrity/provider-drawing1-verification.json)为1/8后length失败；[drawing2/qualification3](../capability-evidence/diagnostics/f5-lmstudio-completion-integrity/provider-drawing2-verification.json)为2/8后一次恢复再length失败；[drawing3/qualification4](../capability-evidence/diagnostics/f5-lmstudio-completion-integrity/provider-drawing3-verification.json)为2/8后第三轮超16工具轮，没有任何length或截断恢复事件。第三轮成功查询的完整CylinderFace实际保留在请求23/24，模型却反复提交缺stateId或非完整path的查询。可以排除这两条请求缺几何选择的假设，不能宣称制造工作流已修复。两次修订和原失败均保留，当前工作区新差异仍未验。

全部根模型作业终态后已实查消费者空闲，明确交还现有SDK final DA数小时原生/窗口及全量CPU时段；根防护拒绝以旧224返还解锁。SDK继续生成器迁移及新包资格，开发仍用未改变的精确87.cu1，没有收到新Preview87交付。当前[检查点](../capability-evidence/diagnostics/f5-lmstudio-completion-integrity/checkpoint.json)保存小型TRX、构建/资格和D6失败记录，不上传二进制或缓存。A–F其余范围保持，G与清理暂缓。
