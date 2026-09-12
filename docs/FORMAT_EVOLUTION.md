# M3-V：格式演进与存储基准

实施与验证日期：2026-09-12。当前存储使用 MessagePack 3.1.8、OcctSharp 8.0.1-preview.26、OCCT 8.0.1。本阶段完成资产格式目录、跨编码/跨节迁移、固定旧文件兼容和存储测量；完整 M3 的文件系统故障矩阵仍未完成。

M4-S1 后续更新：当前格式已在此基线上增加 sketches v1，将 document 提升为 v3，写出六节和 applicationVersion=0.3.0。本文以下协议与性能数字保留为 M3-V 的历史验收记录；当前草图迁移和证据见 [SKETCH_FOUNDATION](SKETCH_FOUNDATION.md)。

## M3-V 写出协议

ZIP `containerVersion=1` 保持不变，`assetCatalogVersion=1`，`applicationVersion=0.2.0`。新增必需能力 `cadoryx.geometry-table.1`、`cadoryx.asset-catalog.1`，让旧读者明确拒绝不理解的新文件。升级后的文件不保证能被旧应用打开。

| 节 | schemaVersion | encoding | 内容 |
|---|---:|---|---|
| document | 2 | messagepack | DocumentId、StateId、根定义、单位和公差 |
| presentation | 2 | messagepack | 图层、材料 |
| structure | 3 | messagepack | 定义、实例、体；体以修订 ID 引用几何 |
| features | 3 | messagepack | 配方、依赖、输出属性；结果和源以修订 ID 引用几何 |
| geometry | 1 | messagepack | 修订 ID、AssetId、类型、包围盒、体积和源 XDE 引用 |

`GeometrySections.cs` 保留旧 v2 DTO，使用独立 v3 契约。geometry 表以 `GeometryRevisionId` 去重，重开后体、特征结果、导入配方源共享同一个不可变 `GeometryAssetRef`。同一修订出现不同定义、重复/缺失/未使用的表记录均拒绝；相同字节的不同几何修订仍可独立存在。

MessagePack 使用显式数字 Key、配方白名单、UntrustedData、深度 128、禁用 Typeless/Contractless 和内部压缩。字段编号不得重排或复用。容器版本、节版本、资产格式版本、配方版本相互独立。

## 资产格式与来源

每个清单资产都有长度、SHA-256 和 `AssetFormat`：

| 字段 | 新 BRep | 新 XDE |
|---|---|---|
| mediaType | application/vnd.opencascade.brep | application/vnd.opencascade.xde |
| encoding | brep-ascii | binxcaf |
| formatVersion | 3 | 12 |
| kernel / kernelVersion | OCCT / 8.0.1 | OCCT / 8.0.1 |
| writer / writerVersion | OcctSharp / 8.0.1-preview.26 | OcctSharp / 8.0.1-preview.26 |

领域中的格式跟随 `GeometryAssetRef.Format` 和 `XdeSourceRef.Format`；资产仓只保存内容寻址的字节和租约。这样同时打开两个文件时，同一 AssetId 的旧来源不会被另一个文件覆盖。保存时若同一内容引用了互相冲突的描述，会报错而非任选其一。

旧资产没有原始内核/写出库版本：通过 BRep/FSD 头识别编码与文件版本，保留 `kernel=OCCT`，生产者版本保持 null。升级保存不重新编码几何，也不把“本次读取使用的版本”冒充“原始生产者版本”。普通建模生成的新结果才使用当前写出版本。

当前兼容策略接受 BRep ASCII 格式 1–3、BinXCAF 12，已声明的 kernelVersion 只接受 8.0.1；缺失版本按旧文件策略处理。实际固定文件的原生解码验证覆盖 BRep v3 / XBF v12；没有宣称其他 OCCT 发布版本的完整互通。BRep 的 Topology 标记、XBF 的 BINFILE/字节序/FSD 版本/BinXCAF 标记与清单不符时，在进入原生解码前拒绝。

未知可选节导致文档只读，原始载荷及附属资产/格式一起保留。仅由未知扩展引用的未来格式不会被原生解码，即使它声明未来 OCCT BRep。当前模型实际依赖的资产必须通过角色、版本、头部和 hash 校验。旧可选资产没有格式时标记 opaque。没有扩展节承载的孤立资产拒绝保存/打开，避免重存静默丢失。

## 迁移管线

`CadSectionMigrationRegistry.RegisterStep` 显式声明输入和输出 `(kind, version, encoding)`。当前内置路径：

```text
document     v1/json → v2/messagepack ──────────────────────┐
presentation v1/json → v2/messagepack ──────────────────────┤
structure    v1/json → v2/messagepack ─┐                    │
features     v1/json → v2/messagepack ─┴→ 联合提取几何表 ────┤
                                        structure v3      │
                                        features v3       ├→ 当前快照
                                        geometry v1 ──────┘
```

规则按注册顺序执行；同一输入契约不能对应多个迁移，原有节必须提升版本。每步的全部输出合同和容量检查通过后，才替换局部不可变节集合。依赖不全、缺迁移、额外/错误输出、覆盖非输入节、异常、取消都会终止加载；已经暂存的资产租约全部释放。最多 64 步。注册的迁移回调属于受信任的代码，须遵守不修改输入字节的契约。

默认限额：单结构节 32 MiB、清单 8 MiB、单资产 256 MiB、总解压数据 1 GiB、20 万 ZIP 条目。迁移前后均检查每节和总量，不能绕过存储限额。每节仍完整解码到内存；限额不是流式读取的保证。

`ReadSettingsAsync` 只读取清单和 document 节，并仅迁移该节。其他节存在未知版本或损坏载荷不要求它解码；ZIP 路径/总容量和清单本身仍必须合法。当前没有公开通用 `ReadManifestAsync` / 任意节读取 API。

原 JSON `Register/Read` 辅助 API 为兼容调用者保留，实际 `CadDocumentStorage` 使用 `RegisterStep/Migrate`。新协议需要新增明确规则及旧文件样本，不会从版本号自动推断升级方式。

## 保存点与失败语义

加载迁移保持业务 ID、StateId、资产字节和引用，不重跑建模；已保存文档不会因内存迁移变脏。诊断分别记录 `IO.MIGRATED`、`IO.LEGACY_ASSET_FORMAT`；未知扩展记录 `IO.READ_ONLY`。保存写当前格式；另存为可保留旧版副本，普通保存可以覆盖原路径，没有自动永久备份。

保存继续使用同目录临时文件、完整清单/载荷校验、刷新和最后原子替换。取消/异常不改变旧目标与保存点，临时文件清理。异步保存期间的新编辑仍保持脏状态。资产写出使用租约 ReadOnlyMemory；回读校验用 64 KiB 池化缓冲区和增量 SHA-256，避免额外整资产副本。打开按条目长度分配精确缓冲区，但 MemoryAssetStore 暂存仍复制一次。

## 固定样本与验收

[Fixtures/Storage](../Cadoryx.Tests/Fixtures/Storage/README.md) 保存三个不会在测试中重建的旧文件：v1 JSON box、v2 MessagePack box、v2 彩色装配。v2 文件来自升级前实际 WPF 产物；v1 文件在修改领域/存储代码前按原始 JSON DTO 编码生成，属于兼容性合成样本，不是历史终端用户文件。

自动化新增 29 项，覆盖身份与精确资产、共享修订、错误格式/版本/头部、修订冲突、跨节失败/取消/输出限额、设置单节读取、未来可选资产透传和目标保护。全部 120 项通过，0 跳过；Release 构建零警告、零错误。

```powershell
./scripts/verify.ps1 -PublishSmoke -WindowSmoke -RecoverySmoke
```

最终发布目录 `artifacts/publish/20260912-144621`。常规、窗口、恢复证据分别为 `artifacts/smoke-20260912-144621`、`artifacts/window-smoke-20260912-144621`、`artifacts/recovery-smoke-20260912-144621`。三个 result.txt 为 PASS，四个绑定日志为空。窗口入口现在先通过 MainWindow 打开三个旧文件，保存当前格式并关闭/重开，验证 ID/状态/体/资产、五节格式和面颜色；原文件 hash 不变。每次关闭后 native 宿主和资产计数为零。

还回归三格式导出、资源操作、12 轮浮动/重停靠和真正终止进程后的恢复。当前对话内容由 DialogHost Popup 承载，诊断按 DialogSession.Content 定位并等待内容可见，避免把主窗口视觉树搜索失败误判为功能失败。已查看升级后原生颜色、中文资源页和恢复页截图。

## 大文件读写基准

复现：`./scripts/benchmark-storage.ps1`。源码位于 `tools/StorageBench`，有独立锁文件；每类样本使用独立进程。最终结果：`artifacts/storage-bench-20260912-144824`，含三个文件及逐次 JSON 测量和独立设置读取证据。

环境：Windows build 26220、.NET 10.0.12、Intel64 Family 6 Model 183 Stepping 1、24 逻辑处理器，本机文件系统。一次预热保存后测三次保存/打开/设置读取，表中为三次中位数；几何构造不计入时间。打开包含身份、体、实例数、资产字节规模验证和释放。不强制 GC；有文件缓存、JIT 和系统负载影响，没有冷缓存或 JSON 对照声明。

| 样本 | 原始资产字节 | 文件字节 | 保存 ms | 打开 ms | 设置 ms |
|---|---:|---:|---:|---:|---:|
| 25,000 个共享实例，1 个 box BRep | 2,565 | 1,101,957 | 65.9 | 64.5 | 0.61 |
| 8,000 个独立 box 构成的 compound BRep | 25,218,476 | 5,356,507 | 101.9 | 56.4 | 0.73 |
| box + 128 MiB 不可压缩扩展载荷 | 134,220,335 | 141,562,229 | 1,726.2 | 605.3 | 0.99 |

第三项是存储压力样本，不代表大型 CAD 模型、面数或绘制性能；它含一份扩展节和两项资产。不可压缩数据使用当前 ZIP Fastest 后体积略增。所有样本往返后源/目标资产计数归零。

| 样本 | 保存分配 MiB | 打开分配 MiB | 打开过程最大私有内存增量 MiB | 打开私有内存峰值 MiB |
|---|---:|---:|---:|---:|
| 共享实例 | 16.77 | 32.18 | 12.01 | 99.59 |
| compound BRep | 0.44 | 48.35 | 50.18 | 837.63 |
| 128 MiB 扩展 | 5.34 | 260.95 | 264.36 | 1,098.80 |

分配是 `GC.GetTotalAllocatedBytes` 的过程增量中位数，包含测量开销；进程私有内存每 10 ms 采样并取最大值，不是精确瞬时峰值。绝对峰值包含此前几何构造、原生分配器和未回收托管堆，不能当作文件的独占常驻内存。分配约为原始资产两倍，指向读入缓冲区 + Stage 拷贝；下一步应评估资产接管/流式或磁盘存储，而不是提前扩大并行 worker。

单独 `--settings-only` 进程读取该 BRep 文件，`settings-only.json` 记录 `nativeKernelLoaded=false`。IO 程序集也没有 OcctSharp 引用；设置读取不依赖 native 初始化。

## 后续范围

M3-V 之后已完成 M4-S1 草图模型与求解基础，并通过本迁移框架加入草图节。当前交付边界见 [SKETCH_FOUNDATION](SKETCH_FOUNDATION.md)，下一阶段 M4-S2 的交互和关联特征重算任务见 [ROADMAP](ROADMAP.md)。

M3/M6 余项包括磁盘满/权限/断电及网络文件系统故障矩阵、真实大型导入模型、首帧和交互测量、资产内存预算及按需读取。混合 DPI、RDP、长期窗口循环和全新机器部署仍是发布门禁；本机通过不替代这些验收。
