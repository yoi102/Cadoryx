# B9：精确作者曲线及编辑实施记录

2026-10-02，A–F 总目标持续进行；G 实机、安装及交付按用户要求暂缓。本页区分实现、核心测试、WPF 自动化、原生视口、物理输入及外部 CAD。

## 实现范围

- 新作者三次样条使用控制点 ID、明确结点及重数，支持 4–1024 控制点、非均匀结点和任意合法控制多边形。无明确结点的旧曲线继续使用原来的 4–16 点受限均匀语义。
- 无启用约束的草图直接保留经过领域校验的作者坐标，不分配稠密求解系统，能正常确认并保存 1024 控制点。含启用关系的数值求解仍遵守已有默认 256 变量/512 方程预算，超预算明确拒绝，不承诺任意规模的约束求解。
- 曲线结构合法与可建模区域合法分开：平坦、自交或首尾同位置的合法样条可保存在草图中；实体区域须通过原生精确 BRep、干涉、包含及孔岛检查。显示和命中采样不能证明区域合法。
- 拉伸、旋转、混合边界、孔及递归岛、扫掠截面/路径、放样通道、反向、端点 G1、缓存比较和重放模板消费原结点。旋转使用逐结点区间的解析极值检查内部穿轴/切轴。
- Bézier 和样条可按归一化区间精确保留/移除，或按有限原生交点点选修剪。样条使用结点插入与提取，Bézier 使用多项式重参数化，不作折线拟合。
- 延伸保持原曲线，采用明确的端部多项式延续，量在 `(0,4]`；点选延伸寻找该范围内最近的有限边界。重合区间、等价最近投影和交点处选择明确拒绝，失败保持草稿及历史不变。
- 单个存活片段保留曲线 ID；多个存活片段全部使用新 ID。仅明确保留的原端点复用点 ID，清理孤立控制点及约束。缺失作者边界使关联拉伸/旋转、扫掠/放样及后继冻结，保留旧结果并清除拓扑历史，作者须显式重选。合法曲线的几何失败仍原子拒绝，不用冻结掩盖所有错误。
- 草图投影保持自身契约：投影来源分裂或删除时整次拒绝，须先解除投影再编辑/重选；不会自动把它绑定到任一片段。
- 生产草图窗口提供结点表、精确保留/移除区间和多项式延伸输入，创建数量上限同步为 1024。长结点文本换行并可滚动；三语言诊断保持同键。分裂预览明确报告冻结的特征。关联旋转和高级拉伸可显式选择替代轮廓；高级拉伸按捕获的完整文档枚举，不再受主工具或当前选中零件的过滤限制。

## 依赖与存储

锁定不可变包 `8.0.1-preview.31.cadoryx.curve-inspection.2`；当前 document34 / features32 / sketches12，assembly-patterns2 / datums2 不变。新结点字段覆盖 sketch、recipe、mixed boundary、spline holes、递归 islands、guided 和 extrusion replay template。逐级迁移拒绝旧 schema 夹带结点或超旧控制点上限，并拒绝新版本内不匹配配方类型的结点 companion。

上游增加复制值的 `SketchModeling.InspectIntersections` 与 `Shape.InspectAdaptiveProperties`，不向领域或 Editor 暴露原生句柄。测量对有结点分段的形状使用副本；无分段形状保留原解析积分。Debug 下某些 degree-one extruded spline face 的 divider 部分结果无效，测量丢弃无效结果并使用有效几何副本的 span-aware GK，包含重心和惯量。`.1` 暴露的圆形体积回归由 `.2` 修正；失败日志和两套不可变包保留。

隔离输入及重建脚本：`artifacts/build-b9-isolated.ps1`、`artifacts/finalize-b9-source.ps1`。最终输入清单包含 1219 个源码文件；AG generated hashes 保持不变。原工作区的 AH 生成冲突只在隔离 Runtime csproj 移除相应 Shared Compile 输入，不手改生成文件。正式 forwarders freshness 4151，PASS。

## 当前证据

| 边界 | 结果及位置 |
|---|---|
| 上游真实 Release / Debug | 分别 671/671 PASS；每套 62 DLL 与测试目录逐个哈希匹配，bridge hashes 不同；隔离目录 `OcctSharp/artifacts/cadoryx-b9/isolated-source-1` |
| 独立锁定 NuGet 消费者 | `.2` 48/48 PASS，62 DLL、加载路径及 package identity：`artifacts/occt-capability-b9-2/package-audit.json` |
| 首批作者专项 | 57/57 PASS：`artifacts/b9-authoring-3.log` |
| 编辑/来源联合 | 67/67 PASS：`artifacts/b9-integration-2.log`；递归存储、控制数、重复结点及旧关联规则修正后 22/22 PASS：`artifacts/b9-integration-3.log` |
| 锁定还原及 Release | `artifacts/b9-locked-restore-1.log`、`artifacts/b9-build-12.log`：PASS，零警告/错误；求解器 `cadoryx.sketch.lm-svd.6` |
| 三语言生产控件 | 最终版 `artifacts/b9-curve-editor-smoke-5` PASS，空绑定日志、关闭资产归零，27 张截图逐张检查。按钮为明确 WPF Automation Invoke/Toggle，包含真实移除区间按钮、冻结提示和拉伸轮廓重选；版本 4 暴露主工具过滤导致片段缺失，生产问题已修复。版本 1 的折叠 Expander 查询失败和各轮失败均保留 |
| 编辑联合与新增边界 | `b9-combined-2` 111/111 PASS；新增闭合/等距投影歧义、内部精确切轴、递归孔岛重放及跨工具/零件重选。`b9-boundaries-1` 为 57 PASS / 2 FAIL，两个新夹具漏刷新提交后 revision；修正后 `b9-reselection-2` 2/2 PASS；最终全量也覆盖这些用例 |
| 全量回归 | 最终 `artifacts/b9-full-4/b9-full-4.trx`：1192/1192 PASS，零跳过。上一版1187/1187亦通过。首轮1165/4、第二轮1179/1及联合87/1的失败保留；旧语义/夹具和位于曲线外侧的穿轴反例已修正，没有削弱生产拒绝契约 |
| 物理指针、外部 CAD | 未验证；自动化与原生视口截图不等于实际鼠标或外部 CAD 往返 |

同版 `artifacts/publish/b9-20261002-5` 的 B9、B8、桌面、旧文件/窗口、C1–C4、装配重复、D1–D3 和工程图/PDF 共十三组全部 PASS，十三份绑定日志均为空，各窗口关闭资产归零；已有窗口手势样本本轮 PASS。审计及发布二进制哈希：`artifacts/b9-final-verification.json`。本批本地契约完成，整个 A–F 仍进行中；G 暂缓。

非均匀独立几何例：控制点 `(8,0),(20,2),(16,4),(18,6),(16,9)`，结点 `[0,.2,1]`、重数 `[4,1,4]`；精确面积 40.208，拉伸 3 的体积 120.624。控制多边形非单调或跨弦本身不作为拒绝依据；实际自交、无正区域及非法拓扑才拒绝建模。

## 后续工作

B9 最终门禁后继续多环/孔岛偏移、实体边及椭圆投影、非共享点/曲率关系、A 固定性能、C 螺纹、D 曲面终止、E 多边/回滚/历史/装配和 F 制造/交换/命令。具体范围继续以 [A–F 总跟踪](AF_IMPLEMENTATION_TRACKER.md) 为准；本页完成不能代替整个用户请求完成。
