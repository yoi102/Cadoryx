# 当前993 / SDK25验收接续

本页记录已验的993项来源候选，依赖`8.0.1-preview.31.cadoryx.curve-inspection.25`。完整Release和Debug各5802/5802已独立核对，零失败、零跳过；应用Debug使用包内Release原生库，不能当作SDK真实Debug原生验收。源码和既有断言在这组验收期间保持冻结，已逐哈希[归档993来源](../artifacts/b18-app-current993-source-freeze-1/source-manifest.json)。当前997/SDK26接入草稿另见[接入实现和门禁](B18_APP_OCCURRENCE_INTEGRATION_PLAN.md)，本页结果不能自动作为新候选验收。

## 执行顺序和原条件

两个配置的完整5802已得到终态并独立审计；同载荷WPF候选已发布并核对17个共享应用程序集、13个SDK程序集和62个原生库。固定MiniPC3的两次direct和两次生产Open、材质组件及晚期导入DLL两故障已串行完成。通用第三轮已在用户确认空闲后完成，实际前台与私有恢复目录核对通过；圆盘专项待用户本轮空闲回复。所有构建、原生测试、图形窗口、性能和供应商运行继续串行，不终止用户或无关任务的进程。

| 验收 | 保持的条件 | 当前状态 |
| --- | --- | --- |
| 完整回归 | 原5756加新46，零失败、零跳过，用例名称和来源/载荷匹配 | 两配置各5802通过，相关670保持 |
| 圆盘 | 7个滚轮序列，400ms内真实桌面像素切页，采样0指针事件，后续移鼠不改页；180秒总期限 | 通用第三轮7次通过，35.352–52.858ms，0指针事件，实际前台始终等于Cadoryx owner；独立专项未运行，首轮前台FAIL保留 |
| 通用窗口 | 原旧文件迁移/颜色/选择/覆盖/撤销/重开，12浮动/停靠/关闭周期，60完整PNG；240秒期限 | 第三轮110.715秒通过、绑定日志空、实际62匹配；60图中19变化完整图直接审阅、41完整字节复用。首两轮FAIL及首次审计时间序列化FAIL保留 |
| direct | 2个新进程，每个20次双视口循环；4964实例、1185几何，每视口1185来源/1个XDE上下文；6482ms目标和1800秒期限 | 独立审计通过，完整提交5779.4878/5237.6599ms，均达原目标 |
| 生产Open | 2次，900秒期限，原MiniPC3字节/哈希和788×411整张PNG严格比较 | 独立审计通过，完整场景65126.2682/68464.0239ms，整张PNG符合原哈希 |
| 材质组件 | 原21项：6槽×Phong/PBR共12、DXT正/截断6、无效渲染3；76完整PNG，原断言保持 | SDK25的21项/76完整图独立审计通过，不代表应用材质创作窗口 |
| 晚期导入DLL故障 | 主窗口和真实原生视口可用后，真实导入工作器分别遇到物理缺失和无效DLL；检查SDK加载异常、父窗口报告、原快照/资产/历史、临时任务清理及后续建模 | 私有诊断目录复验2项通过，3完整视口图已审阅；进程内渲染故障仍未证明 |

圆盘使用应用排队的HWND消息和已呈现桌面像素，不证明硬件滚轮延迟。所有图像比较保持原始完整字节，不缩放、裁剪或增加容差。全量通过不自动提升A5或A–F的完整完成状态。

## 晚期DLL故障夹具边界

私有`DOTNET_STARTUP_HOOKS`只在实际`--import-worker`子进程中设置`APP_CONTEXT_BASE_DIRECTORY`，指向验收输出下的空运行目录或含无效DLL的目录。必须记录实际生效的目录并观察未修改的SDK解析器抛出`DllNotFoundException`；无效DLL还必须记录真实`BadImageFormatException`原因。不在夹具里伪造异常，也不修改生产DLL、SDK或用户运行目录。

父窗口通过实际`OpenPathAsync`导入。失败后必须保留同一个文档快照、所有原资产字节、代次和撤销/重做统计，报告真实失败目录一次，并完成原生产导入任务目录的清理。后续建模、撤销/重做和正常保存关闭继续执行，最终host、资产和原生来源归零。夹具源码、构建日志和首次失败结果冻结保留。

这两种情况证明晚期**导入工作器**加载故障。它们不证明进程内渲染器在运行中遭遇DLL故障、完整GPU故障矩阵或应用PBR/纹理创作窗口。当前`CadMaterial`的应用契约是密度与质量；SDK材料组件的通过不能冒充完整应用材质功能验收。

## 证据入口

- [通用第三轮独立验收](../artifacts/a5-current993-general-window-3-verification-2.json)
- [60完整图审阅](../artifacts/a5-current993-window-images-3-verification.json)
- [实际前台与目录](../artifacts/a5-current993-general-window-3/desktop-owner-activated.json)
- [首次审计时间序列化FAIL](../artifacts/a5-current993-general-window-3-audit1-failure.json)
- [SDK26应用接入决定](B18_APP_OCCURRENCE_INTEGRATION_PLAN.md)
- [最新多文档通过/工程图失败/原生机制证据](../artifacts/a5-current993-status143-verification.json)
- [当前证据汇总及旧占位字段覆盖关系](../artifacts/a5-current993-status142-verification.json)
- [当前供应商同载荷候选](../artifacts/f5-current993-provider-build-verification-1.json)
- [原模型/预算供应商复验准备](../artifacts/f5-current993-provider-runner-preparation-1.json)
- [完整Release5802](../artifacts/b18-app-sdk25-release-full5802-1-verification.json)
- [完整Debug5802](../artifacts/b18-app-sdk25-debug-full5802-1-verification.json)
- [完整Debug日志](../artifacts/b18-app-sdk25-debug-full5802-1.log)
- [当前性能/窗口脚本准备](../artifacts/a5-current993-acceptance-preparation-1.json)
- [窗口独立核对脚本准备](../artifacts/a5-current993-window-audit-preparation-1.json)
- [SDK25材质组件准备](../../OcctSharp/OcctSharp/artifacts/cadoryx-a5/material-current993-sdk25-1/preparation-1.json)
- [材质运行/核对脚本准备](../artifacts/a5-current993-material-runner-preparation-1.json)
- [当前性能独立审计](../artifacts/a5-current993-performance-verification-1.json)
- [当前材质21项/76图独立审计](../artifacts/a5-current993-material-verification-1.json)
- [实际晚期导入工作器故障2项独立审计](../artifacts/a5-current993-late-import-fault-verification-2.json)
- [故障视口完整3图审阅](../artifacts/a5-current993-late-import-fault-images-2-verification.json)
- [通用窗口首轮前台FAIL](../artifacts/a5-current993-general-window-1-failure-verification.json)
- [通用窗口次轮菜单FAIL](../artifacts/a5-current993-general-window-2-exit.json)
- [第三轮启动目录/前台夹具准备](../artifacts/a5-current993-controlled-general-preparation-3.json)
- [晚期故障源码](../artifacts/a5-current993-late-import-fault-1/Hook/StartupHook.cs)
- [晚期故障运行脚本](../artifacts/run-a5-current993-late-import-fault-1.ps1)
- [晚期故障独立核对脚本](../artifacts/verify-a5-current993-late-import-fault-1.py)

正式已验收仍为A4 / B17 / C5 / D5 / E4 / F5g。原模型当前多文档8轮已通过，工程图第三轮失败保留；完整身份组合和其余A–F继续；G按用户要求暂缓。
