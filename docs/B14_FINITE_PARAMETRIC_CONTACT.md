# B14 有限参数自由曲线接触实施记录

2026-10-04。本地有界功能已实现，专项、窗口及独立消费者已验证，最终全量仍在收取；整个 A–F 继续，G 实机、安装与交付暂缓。

两条不同作者曲线在各自明确的内部归一化参数处保持位置和 G1 切线关系，方向明确为同向或反向。支持线段、二次 Bézier、夹持非有理三次样条；求解控制点，不移动作者参数、不合并点或曲线身份。三方程解析 Jacobian、独立原生位置/导数 oracle、毫米空间复证与原有 256 变量/512 方程预算均保留。端点、内部多重结点的左右侧歧义、退化导数与错误切线分支拒绝。

原子草图编辑、两个接触参数的 C1 驱动、后继实体及工程图、撤销/重做、独立作者复制、修剪/分裂/延伸时删除旧接触、保存重开和独立迁移已接入。document49 / sketches19 / features41 / drawings14 / history4，solver `cadoryx.sketch.lm-svd.9`，能力 `cadoryx.sketch-finite-parametric-contact.1`。三语言生产控件与结构化作者可编辑参数和方向；终端 `SKETCH_FINITE_CONTACT` 保留严格原始 JSON。

最终联合 `b14-contact-focused-5` **337/337 PASS，1m06s**，包含 B14/B13、迁移、工具、Model/Datum/Drawing/Parameter Agent。`focused-4` 的 335 PASS / 2 FAIL 保留：终端 Tokenize 未保留此动作的原始 JSON、DrawingAgent aggressive8192 工具预算漏掉 cad_datum。已修正原始 JSON 名单并压缩工具描述；十二工具、strict schema、原安全与输出预算未提高。build-16 的最后变化只有两个 C5 当前 document49 断言；focused-5 测试程序集在这两个断言之前，生产共享程序集与最终候选完全相同。

故意扣住原生结果的 Agent 测试夹具现在先启动会话取消，再释放 held result，最后等待清理。Datum/Drawing/Parameter 与 Model 放在禁并行 native-gates collection。新增三项失败清理测试，保留原就绪期限、清理预算、原子性、资产归零要求。旧 A4 全量 DLL 不含这些后来修复，不能把后续专项通过追记到旧轮次。

冻结 `b14-contact-20261004-4` 的八组窗口全部 PASS：B14、desktop、B13、C1、E1、drawing、A4取消、C5实体螺纹。十二张三语言 B14 截图逐图直接检查；英文约束长标签改为换行后完整显示方向。预期 Confirm 关窗标记在命令前设置，修正候选1的诊断误报。用户说明部分窗口手动关闭，具体旧轮次未知；早退、超时和无结果均不能据此改成通过或确定归因。

独立 `FiniteContactProbe` 只引用冻结应用 DLL，重开实际窗口保存的十二场景，以独立 OCCT 精确边核对位置/切线，驱动 tA .46→.43、tB .54，验证 Bézier 实体解析体积、关联 Top 工程图、endpoint1 原子拒绝、撤销/重做及保存重开，资产归零。消费者执行候选3；八个消费者 DLL 与候选4相同，全部十七个共享应用 DLL 也相同。候选4 WPF 的单独窗口证据不借用候选3结果。三页三语言矢量 PDF 均直接审阅，0 raster image；小轮廓与远处支撑盒是关联/导出样本，不作制造排版验收。

`artifacts/b14-binary-audit-1.json` 核对十七共享应用/两 WPF、十三 SDK 托管/六十二原生 DLL、十四不可变包、三语言 1304 个非空资源键及八份空 bindings.log。明确保留最终全量 PENDING。旧 build-7 失败后 `focused-2` 的 93 PASS 来自旧 DLL，已在审计中排除；build-8 的 focused-3 105 PASS 才是有效后续结果。build-9 window 变量、build-11 AddSpline 重载歧义等编译失败也保留。

候选3 desktop 通知可见性 FAIL 的原因未证明；候选1与候选4 desktop PASS，不将重复运行通过写成已定位生产根因。A4 full1 2366 PASS / 1 FAIL 且 Aborted、A4 full2 2313 PASS / 0 FAIL 且 Aborted、C5 modeled-full2 2373 PASS / 0 FAIL 且 Aborted 均保留。B14最终全量使用不可覆盖的 build3输出，在旧 A4 testhost 退出后启动。

仍未完成：圆形/椭圆与自由曲线混合接触、非端点 G2、参数自动求解及偏移连接；A真实性能/输入、D曲面终止、E曲面移动/替换/缺历史、F制造规则/外部元数据/供应商失败继续。下一批 [D5有限曲面终止](D5_FINITE_SURFACE_TERMINATION_PLAN.md)已取得三种几何正例与过小平面负例的 SDK 前提，不代表应用与完整分支证明完成。

2026-10-05 最终收取：B14 冻结 build3 的 full1 已正常结束，**2461 PASS / 1 FAIL，2462 total，42m52s**。唯一失败为 `SketchNativeProjectionEditorTests.LateNativeResultCannotEnterDraftAfterGenerationChangeOrClose(close: True)` 25 秒 TimeoutException；旧 TRX/日志及原审计当时 PENDING 保留，补充结果见 artifacts/b14-final-regression-1.json。当前夹具把 Picked 信号放在真实原生返回后并禁并行，原期限未放宽；修复后的 7 项原生投影已包含于冻结 D5 最终 168/168 联合。D5候选4九窗口（含 A4 取消）及十二消费者通过，最终冻结完整回归已启动；专项/窗口不能替代全量，整个A–F继续，G暂缓。
