# 自动测试与覆盖率

更新：2026-09-29。本页记录 **xUnit 测试进程实际加载的托管代码** 的覆盖率。测试通过数、行/分支覆盖率和桌面/环境验收是三种不同证据，不能互相替代。

## 复现方法与统计口径

在仓库根目录、Windows x64 和固定的 .NET 10 SDK / OcctSharp 本地包环境中运行：

```powershell
pwsh -NoProfile -File scripts/coverage.ps1
```

脚本运行 `Cadoryx.Tests` 的 Release 全套测试，使用 Microsoft Code Coverage 采集 Cobertura XML，在同一结果目录生成 `summary.json`，并校验逐程序集行数、分支数之和与 XML 根节点一致。已有 XML 可用 `-ReportPath <绝对路径>` 重新汇总，无需重复跑测试。脚本不修改测试项目的包依赖。

统计口径有三层：

1. 原始报告包含 `Cadoryx.Tests` 与 `CommunityToolkit.Mvvm`，仅用于核对采集器输出，不作产品覆盖率。
2. “Cadoryx 生产程序集”只纳入名称为 `Cadoryx.*` 且不是 `Cadoryx.Tests` 的程序集；此口径包含生成的 `Cadoryx.Lang`。
3. “排除生成式本地化”进一步剔除 `Cadoryx.Lang`，便于观察主要业务代码；它仍不是整个应用的覆盖率。

行覆盖率按 Cobertura 的方法行计数，分支覆盖率按条件分支计数；二者均以有效项数加权汇总，**不能对各模块百分比取算术平均**。WPF 可执行程序和控件程序集没有进入此 xUnit 进程，原生 OCCT 代码也不在 .NET 覆盖率内。发布版桌面冒烟、真实 Codex/LM Studio 探针和手工操作另行取证，没有并入这些百分比。

## 本轮结果

本轮 Release 完整测试 **618/618 通过，0 失败、0 跳过**。原始报告为 `artifacts/coverage-20260929-212729/60ce0402-7638-4881-874f-204fedf97df8/yoiri_YOIRI_2026-09-29.21_32_50.cobertura.xml`，同目录 `summary.json` 可供脚本读取。

| 统计范围 | 行覆盖 | 分支覆盖 |
|---|---:|---:|
| 原始 Cobertura（含测试代码和第三方生成代码） | 23,608 / 33,285（70.9%） | 12,537 / 18,897（66.3%） |
| Cadoryx 生产程序集（含生成式本地化） | 15,665 / 23,367（67.0%） | 10,727 / 16,213（66.2%） |
| 排除 `Cadoryx.Lang` 后的业务程序集 | 15,661 / 20,668（75.8%） | 10,726 / 16,203（66.2%） |

| 程序集 | 行覆盖率 | 分支覆盖率 |
|---|---:|---:|
| `Cadoryx.Agent` | 75.5% | 68.6% |
| `Cadoryx.Agent.Codex` | 61.5% | 48.5% |
| `Cadoryx.AI.Contracts` | 77.4% | 50.0% |
| `Cadoryx.AI.LmStudio` | 41.4% | 28.0% |
| `Cadoryx.Cli` | 74.6% | 66.4% |
| `Cadoryx.CommandLine` | 88.6% | 83.9% |
| `Cadoryx.Commands` | 84.8% | 72.9% |
| `Cadoryx.Db` | 84.6% | 74.2% |
| `Cadoryx.Editor` | 88.5% | 77.3% |
| `Cadoryx.IO` | 85.5% | 77.0% |
| `Cadoryx.Kernel.Abstractions` | 74.3% | 83.6% |
| `Cadoryx.Kernel.Occt` | 94.2% | 81.1% |
| `Cadoryx.Lang`（生成式本地化） | 0.1% | 10.0% |
| `Cadoryx.Rendering` | 77.9% | 81.9% |
| `Cadoryx.Rendering.Occt` | 6.5% | 4.9% |
| `Cadoryx.Sketching` | 86.6% | 89.5% |
| `Cadoryx.ViewModels` | 61.2% | 52.4% |
| `Cadoryx.ViewModels.Services` | 84.1% | 67.7% |

605 项测试时，生产程序集为 64.5% 行、64.8% 分支；新增协议与上下文测试至 613 项后为 66.6% 行、65.9% 分支；结构化查询/建模至 615 项后为 66.8% 行、66.0% 分支；精确选择至 617 项后为 66.9% 行、66.1% 分支；图纸内部分页至 618 项后为 67.0% 行、66.2% 分支。`Cadoryx.Agent.Codex` 的协议流程测试使其从 0% 到 61.5% 行，`Cadoryx.Agent` 的上下文测试使其从 56.3% 到 75.5% 行；结构化工具扩展令 `Cadoryx.ViewModels` 从 58.7% 到 61.2% 行。这些是**同一测试进程口径**下的比较，不表示用户界面或真实外部服务也达到相同覆盖率。

## 本轮新增的行为测试

- `CodexAppServerClientTests` 用内存 app-server 协议端点验证模型去重与连接重建、`thread/start` 的只读配置、文本/图片输入、动态 CAD 工具的成功和失败回传，以及取消时发送 `turn/interrupt`。测试不需要 Codex 登录或网络。
- `AgentContextTests` 验证窗口缩减时保留最新用户请求、清理悬空工具消息、超长请求保留首尾，以及上下文溢出后只重试一次且不提交失败的助手轮次。
- `AiAssistantTests` 增加服务商切换时拒绝显示已过期的异步回复。
- `CadAgentDocumentToolTests` 增加分页查询、精确零件/实体/特征 ID、选择状态，以及 Box/Cylinder 的文档状态、目标零件、参数拒绝与撤销测试。
- 同一组测试再覆盖装配重名实体的完整路径选择、实体修订匹配、跨文档和过期状态拒绝；`AiAssistantTests` 确认默认上下文预算仍保留查询、选择与编辑工具。
- 图纸查询测试用真实文档会话中的 51 个尺寸验证摘要之外的分页、视图和尺寸 ID 过滤、错误图纸拒绝及状态与来源身份字段。

这些测试覆盖应用与服务的协议和状态逻辑；真实服务模型质量、登录状态、窗口输入和显卡渲染仍需要各自的集成或人工验收。

## 后续补测重点

优先给 `Cadoryx.ViewModels` 的异步文档切换、命令失败/取消和选择同步补行为测试；给 `Cadoryx.AI.LmStudio` 补 HTTP 错误、超时和上下文重试边界；给可脱离 OpenGL 的 `Cadoryx.Rendering.Occt` 视口状态计算补确定性测试。需要真实窗口、显卡或外部服务的路径单列为桌面/集成验证，不以反射调用私有方法或镜像实现的测试来抬高数字。
