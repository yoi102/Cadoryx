# M7 工程交付

## 范围与完成条件

原路线只定义到 M6。2026-09-27 按继续完善路线并实现 M7 的要求，补充三个完整工作流：装配清单、工程审阅报告、可校验交付包。它们使用 M5 的装配数据、M6 的审阅数据及已有交换接口，不修改 OcctSharp ABI 或 `.cadoryx` 十二节协议。

1. BOM：按稳定 DefinitionId 汇总实例数量；完整展开路径保留共享子装配的每次出现；可选择文档可见对象或全部对象；材料质量未知时留空；桌面预览与 CSV/JSON 输出一致。
2. 报告：自包含、可打印的 HTML，包含 BOM、装配层级、固定尺寸值、关联截面状态、装配关系状态和审阅视图目录。正文中英双语，无外部脚本、字体或网络依赖；保留文档和状态身份。
3. 交付：MetroWindow、Ribbon 图标与 CLI 共用服务；原生文档、BOM、报告及可选 STEP/IGES/STL 写成 ZIP，生成逐文件大小与 SHA-256 清单；生成后校验再原子发布；失败/取消保留旧目标，释放资产和临时文件。CLI 可单独验证已收到的包。

验收须包含共享装配、可见性、未知质量、输出转义、真实三格式往返、保存点不变、取消/导出故障回滚、损坏包拒绝、CLI 退出码及三语言真实窗口。全部实现后统一执行；执行证据见下文。

## 使用

主窗口 Ribbon 文件组的包裹图标打开“工程交付”。左侧是 BOM 预览，右侧选择 ZIP、CSV 或 HTML。ZIP 总是带原生文档、BOM 和 HTML；默认附加 STEP，可勾选 IGES/STL，STL 偏差可调。保存对话框负责询问覆盖；MaterialDesign 进度弹窗提供取消。原生导出调用不能强行中断，取消会等当前原生调用返回，之后不发布结果。

```powershell
Cadoryx.Cli bom model.cadoryx --output bom.csv
Cadoryx.Cli report model.cadoryx --output review.html --all
Cadoryx.Cli deliver model.cadoryx --output delivery.zip --iges --stl
Cadoryx.Cli deliver model.step --output delivery.zip --no-step
Cadoryx.Cli verify-delivery delivery.zip
```

输入仍支持 `.cadoryx`、STEP、IGES、STL。`--overwrite` 才替换已有文件；默认磁盘资产，`--memory` 切换内存资产。`--all` 包括文档隐藏对象。退出码 0 成功、1 操作失败、2 参数错误、130 取消。

## 数据与边界

- `BillOfMaterials` 是不可变派生结果，不写入建模历史。展开上限沿用 100,000 实例/128 层。汇总以 ID 为准，同名零件不合并；层级包含装配行，数量只汇总零件出现次数。
- 可见范围遵循祖先实例、Body 和 Layer 可见性。临时视口隔离/隐藏不影响交付范围。原生文档**始终包含完整模型和历史**，不是脱敏导出；界面和报告明确说明。
- 体积使用 `GeometryAssetRef.VolumeMm3`，质量为体积乘 `DensityKgPerMm3`；没有新做精确测量。不扣除实例相交体积。非 Solid、空零件、失效特征、缺材料密度分别保留未知。多体积累和数量乘积溢出拒绝。
- CSV 使用带 BOM 的 UTF-8，稳定英文列名、点小数、字段全引用与双引号转义；文本开头公式字符和制表符加单引号。HTML 全部用户文本编码并设置禁止脚本的 CSP。
- HTML 记录全部持久审阅项目，模型范围仅控制 BOM；不是二维投影工程图，也不是 PMI/关联尺寸导出。相机/视图的完整恢复依赖包内原生文件。
- 包内固定文件名避免来源名称成为路径。`manifest.json` schema 1 包含文档/状态、选项、内核版本、生成时间、文件长度和哈希，以及原有交换损失诊断。SHA-256 证明包内一致性，不是作者签名。
- 验证器不解压到磁盘；拒绝重复、额外、缺失、不安全路径、选项不符和未知版本。最多 8 个 ZIP 条目，清单最多 1 MiB，单文件最多 8 GiB，展开合计最多 16 GiB。验证不代表几何合法性或来源可信。
- `DocumentDeliveryService` 自持所有资产租约、后台生成、同目录独立工作区、关闭 ZIP 后验证、最后一次取消检查后 `File.Move` 发布。桌面还持有会话快照；期间编辑不会混入包，也不标记原文档已保存。
- 当前原生 STEP/IGES/STL 导出信息损失沿用已有规则。此阶段不解决混合 DPI/RDP/数小时运行、复杂闭环装配或通用关联工程图，它们继续保留为独立未验收事项。

## 验证记录

2026-09-27 统一主验收通过：`scripts/verify.ps1 -PublishSmoke -WindowSmoke -SketchSmoke -RecoverySmoke`，锁定还原、Release 0 警告/错误、556/556 测试（M7 新增 22 项）、独立 CLI 发布、常规桌面、窗口、草图真实指针及生产 30 秒强制终止/重启恢复全部 PASS。主日志 `artifacts/m7-acceptance-final.log`；主发布及四组桌面时间戳 `20260927-104430`，五份绑定日志均为 0 字节。CLI 证据为 `artifacts/cli-20260927-104419`，包含单独的 `delivery-result.txt`。

三语言资源各 557 键，证据 `artifacts/m7-l10n.txt`；180 个本地文档链接与 diff 空白检查通过。先前运行的编译缺失引用已修复；`m7-acceptance-v4.log` 的 556 项测试通过，但桌面通知定位在移动/堆叠重排期间未稳定，不能记为完整通过。通知验收现在等待最多两秒内连续三次通过原位置断言，不调用 Reposition，也不放宽几何误差。随后上述统一主验收完整通过。

用用户提供的 `C:\Users\yoiri\Downloads\11WiFi.step` 实测独立发布 CLI：52 个零件定义、96 个零件实例、197 条含装配的出现路径，生成 2,053,015 字节交付包；含原生文档、CSV/JSON BOM、HTML、STEP/IGES/STL 共 7 个载荷，哈希验证通过。完整导入与交付命令耗时约 2.70 秒（此主机一次测量，不是性能承诺），源文件前后 SHA-256 均为 `317298aec394448957f6961b3e85edc7a64a9366406208bdb8f614eaa746cd23`。证据 `artifacts/m7-real-delivery/result.json`、`verify.json` 和 `11WiFi-delivery.zip`；解出的 `review.html`、`bom.csv` 便于检查。

统一主验收后仅将 BOM 数量列改为自动宽度，修复英文表头截断。最终发布 `artifacts/publish/20260927-104804`；三语言窗口截图及交付专项结果在 `artifacts/m7-layout-smoke-20260927-104804`，最终发布版的常规桌面复查也通过，绑定日志为空；日志为 `artifacts/m7-layout-final.log`。已查看最终中文/英文窗口截图，文字对比正常，英文数量表头完整。

没有新增原生能力或改动 NuGet 版本；三格式真实回读、独立发布 CLI 以及外部样本交付均消费当前锁定 `.topology.1`。未重复执行上游完整迁移/发布门禁。混合 DPI、RDP、数小时运行、真实断电/网络盘及完整关联工程图不在本次 PASS 内。
