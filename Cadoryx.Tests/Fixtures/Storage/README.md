# 固定旧版 Cadoryx 文件

H2-B1 冻结 `m4t1h2a-history.cadoryx`：来自 `artifacts/smoke-20260913-104928/history-Fillet.cadoryx`，SHA-256 `cc4722b54c3a96dc9fbd65f9bd470c24ab2fd785d66e89f89dc856240da17528`。MultiInputHistoryTests 验证 history v1→v2 迁移保留 axis-v2 来源、旧字段、状态与几何，重载解析一致；此前九份样本保持不变。

H2-A 冻结 `m4t1h1-history.cadoryx`：`artifacts/smoke-20260913-102750/history-Fillet.cadoryx`，SHA-256 `b007b5576dac2622911eeac9e224278795be605494bccc6d7014842048f4dad7`。旧 full-topology-brep-v1 证据的读取、原样保存、解析与显式重算升级由 TopologyHistoryTests 验证。此前八份样本保持字节不变。

M4-T1-H1 冻结 `m4t2-local.cadoryx`，来自写出器升级前的 `artifacts/smoke-20260912-230828/local-feature.cadoryx`，七节、document v4。SHA-256：`c5b9347bae6e5b46e7d4109781503c221617758846c4ba211f33f3c0e53cbe02`。验证迁移仅添加空历史、显式重算才产生历史，原有七份样本不变。当前八节协议见 [算法历史](../../../docs/TOPOLOGY_HISTORY.md)。

冻结日期：2026-09-12。前三个文件在 M3-V 修改领域类型和存储写出器之前冻结；m3v-colored 在 M4-S1 修改存储写出器之前冻结。测试直接读取文件，不调用当前写出器生成“旧文件”。修改协议时应增加样本，保留这些字节和原始身份。

| 文件 | 来源 | 解析几何预期 |
|---|---|---|
| v2-box.cadoryx | 升级前 `artifacts/smoke-20260912-133836/smoke.cadoryx` | 40×30×20 mm，单体体积 24,000 mm³ |
| v2-colored-assembly.cadoryx | 升级前 `artifacts/window-smoke-20260912-133836/colors.cadoryx` | 单定义体积 6,000 mm³，两个旋转实例，整体/面颜色及源 XBF |
| v1-box.cadoryx | 升级前用 v2-box 的领域快照按原始四节 JSON v1 DTO 编码 | 与 v2-box 相同 ID、状态和 BRep 字节 |
| m3v-colored.cadoryx | M3-V 实际产物 `artifacts/window-smoke-20260912-144621/colors.cadoryx`，五节 MessagePack、资产格式目录 | 单定义体积 6,000 mm³，两个旋转实例，整体/面颜色及源 XBF；迁移后新增空草图表 |

v1 是兼容性合成文件，v2 和 m3v 是实际桌面验收产物；都不是外部 CAD 厂商或终端用户文档。原生成代码仅作证据保存在 `tools/StorageBench/LegacyFixtureWriter.cs.txt`，不会参与构建。不能用当前领域 DTO 重建 v1，新增的格式字段会污染旧协议。

SHA-256：

M4-S2 新增 `m4s1-sketches.cadoryx`，从 `artifacts/smoke-20260912-173843/sketches.cadoryx` 在修改写出器之前冻结；包含 M4-S1 六节协议、稳定草图/约束和冻结拉伸实体。`SketchAssociationTests` 验证旧草图获得可重复初始修订，旧特征不被自动关联，原状态及几何保持不变。

```text
f3000cb7d341ea802f4b501e30fb2482a0016136d13e0ee0eaa5b0442beb5bd8  v1-box.cadoryx
8461b5cfca76615561d4198036a2511fbc13b83bbf16510bcded6f0479a7d04e  v2-box.cadoryx
f7a6a55bb037ce60c0c050912447226803765f3f810b9c6f2c9817ee25f3ad7b  v2-colored-assembly.cadoryx
d8913e5fef1e4e55da67690bdb8741d6377dcafe7fcee4a4625e49e5aed9228d  m3v-colored.cadoryx
7ef68017601b94e031858cd401ee07e0a02b2652a677c95a23cafd5e7806eb46  m4s1-sketches.cadoryx
```

`FormatEvolutionTests` 验证迁移后精确资产、身份、原生解码、当前格式重存和租约释放；`WindowSmokeRunner` 验证实际打开/重存/重开及源颜色。完整协议和证据见 [FORMAT_EVOLUTION](../../../docs/FORMAT_EVOLUTION.md)。

`SketchStorageTests` 直接读取 m3v-colored，验证 document v2 → v3 与空 sketches v1 的迁移，核对稳定身份、状态、几何目录及资产字节。原三份文件仍由原测试和窗口验收覆盖；新增样本不替换它们。当前六节协议见 [SKETCH_FOUNDATION](../../../docs/SKETCH_FOUNDATION.md)。

当前修订及关联特征协议见 [SKETCH_EDITOR](../../../docs/SKETCH_EDITOR.md)。所有五份固定文件均保留原始字节，测试中不得重新生成它们。

M4-T1 冻结 `m4s2-linked.cadoryx`：来自修改写出器前的 `artifacts/sketch-editor-smoke-20260912-182845/linked-sketch.cadoryx`，包含真实草图关联与后继布尔。SHA256：`63f441927e25171e44c7f882e0d139943495337e34cd35644224d09470c7cbd7`。TopologyReferenceTests 验证空拓扑表迁移、原状态/资产和草图关联保留。当前七节协议见 [TOPOLOGY_REFERENCES](../../../docs/TOPOLOGY_REFERENCES.md)。

M4-T2 冻结 `m4t1-topology.cadoryx`，取自修改写出器前的 `artifacts/smoke-20260912-224513/topology.cadoryx`。SHA256：`738eba3cbcb0ced469f1b472b34c24f00bd2b72a7824017569427bc4e642debf`。包含 T1 两条语义/精确修订引用，LocalFeatureStorageTests 验证 features v4→v5 升级保留引用；此前六份样本原字节不变。当前协议见 [LOCAL_FEATURES](../../../docs/LOCAL_FEATURES.md)。
