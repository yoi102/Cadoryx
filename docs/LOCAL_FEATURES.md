# M4-T2：面/边选择与局部建模

2026-09-12 实现，消费现有 OcctSharp 8.0.1-preview.26。当前范围：长方体定义的六面/十二边拾取、引用检查和显式重选，以及单条直边的等半径圆角或等距倒角。独立窗口使用 `mah:MetroWindow` 与 `Cadoryx.DialogWindowStyle`。

## 使用方法

1. 创建长方体，在 Ribbon 的“局部建模”页打开窗口。
2. 在“长方体定义”选择对象。窗口展示定义坐标中的精确形体；修改共享定义会影响它的全部装配实例。
3. 选择“面”或“边”，在窗口的 3D 视口单击目标。受支持的唯一对象高亮；面可用于保存引用，圆角/倒角必须选择边。
4. 选择操作并输入半径/距离，点击“预览”。参数改变或“重新选择”会撤销候选，重新显示源形体。
5. “确认”以新局部特征替换长方体的可见输出；“取消”或关闭窗口不修改文档。
6. 检查已存引用时，先在列表选择它并点击“检查引用”。若修订已失效，选择当前长方体的面/边，再点击“保存 / 重选引用”。引用 ID 和原修订策略保持，目标与来源修订显式更新；这是可撤销的独立编辑。

窗口有英文、简体中文、日文资源。选择类型与操作名称也本地化。当前不支持在局部特征结果上继续圆角/倒角、多边同时操作、变半径或双距离倒角，也不支持对导入曲面、草图拉伸结果执行本窗口的局部建模。

## 原生拓扑与所有权

T1 的 ResolvedSubshape.Index 仍然只是诊断输出，本阶段不消费它来建模。`BoxTopology` 统一语义分类：校验局部平面/直线类型和完整边界范围，保持 T1 的容差与最小尺寸约束。

- 视口开启 OCCT Face/Edge selection mode，用 GetSelectedItems 得到选择副本；只接受一个唯一 Box 语义。视口只向 ViewModel 发出 BoxBoundary 值，不传 native Shape。
- 建模在已有串行队列中重新加载当前精确 BRep，通过 GetTopologyAdjacency(Edge, Face) 获得其唯一边，按相同语义解析；零个或多个候选拒绝。
- 保持源 Shape、邻接表、选中的边及 FeatureOperationResult 在同一个 operation 生命周期内；调用 FeatureModeling.Fillet/Chamfer 后检查操作完成并存储合法 BRep，依次释放所有者。
- 实际包测试证明此图中的唯一边可作为局部算法输入：十二边 × 两种操作均验证体积与 BRep 合法性，且包含任意刚体放置。不能把其他 BRep 加载图、Viewer 选择副本或旧诊断序号传给建模代替这一步。

这是在一个当前原图内使用边的能力。它没有建立操作前后通用的拓扑命名关系；圆角生成面、倒角生成边以及后继布尔结果的持久传播仍由 M4-T1-H 单独验收。

## 领域、命令与重算

`LocalFeatureRecipe` 保存 Source、Box 配方缓存、First/Second 两个边界、Operation 和 Size。缓存必须与唯一上游 Box 特征的当前配方/几何完全一致，DocumentSnapshot.Validate 验证依赖、所属零件、字段枚举、轴顺序及数值。配方保存语义，不保存 native 指针或遍历顺序。

`LocalFeatureCommand` 要求选择来自当前可见输出修订、输出仍存在、生产者是 Box 且图层未锁定。候选包含新 FeatureId/BodyId、原输出元数据、创建时引用及替换后的零件体列表；失败不更改原图和资产。上游仍保留在特征图中，作为局部特征唯一输入。

`FeatureRecompute` 刷新 Source 和 Box 缓存，再重算后继闭包。上游尺寸或放置变化后，局部操作继续作用于同一 Box 语义边。失败时保持全部旧快照；撤销/重做直接恢复历史快照和精确资产，不重新执行圆角。

`LocalFeatureViewModel` 捕获文档及代际，候选拥有临时几何租约。参数改变、取消、文档改变或关闭会作废候选；晚到结果释放。只有相同会话代际才能确认。未知可选节文档沿用只读限制。

局部特征当前由专用窗口创建；既有通用属性面板未提供其参数编辑 UI，底层 RecomputeCommand 已能重算该配方。继续编辑、多个边、生成拓扑重选等扩展不能误认为已完成。

## 文件协议

仍使用七节 MessagePack：document v4、structure v3、features v5、presentation v2、geometry v1、sketches v2、topology v1。containerVersion/assetCatalogVersion 均为 1，applicationVersion=0.4.2，新增必需能力 `cadoryx.local-box-edge.1`。

features v5 增加白名单配方 `local-box-edge`，复用 PackRecipe 的既有字段：Numbers=[X,Y,Z,Size,First,Second]，Placement=Box.Placement，Sources 为一个几何表修订，Operation 为固定枚举。读取拒绝非整数边界、未知枚举、同轴/逆序、错误参数数量、错误输入数量和与上游不一致的缓存。

features v4→v5 是显式无损迁移；v4 出现新的配方标签则拒绝，避免伪装旧协议。之前 features v3→v4 的输出固定为 v4，不随 CurrentFormats 跳版。冻结的 `m4t1-topology.cadoryx` 源于修改写出器前的真实 T1 发布冒烟，SHA256=`738eba3cbcb0ced469f1b472b34c24f00bd2b72a7824017569427bc4e642debf`，迁移保留已有两条引用。

## 验收与边界

本阶段新增 42 项测试，全套 242 项。包含十二条边的两种局部操作、任意刚体放置、原生合法性与理论体积、上游重算、精确历史/保存重开、引用重选、锁定/过期/不支持输出、失败/取消候选、旧文件迁移和损坏协议。实际 T1 与更早固定文件继续回归。

`scripts/verify.ps1 -PublishSmoke` 自动打开主窗口真实命令创建的 LocalFeatureWindow，在 OCCT 原生视口投影坐标处发送 PointerPressed/Released，验证顶面和目标边语义、失效引用重选、MahApps 数值绑定、三语言窗口、预览取消和确认。单边倒角得到 5,980 mm³，保存后导出 STEP/IGES/STL。该方式覆盖视口适配器和 OCCT 拾取，未声称真实物理鼠标输入或混合 DPI 验收。

产物为 local-feature-result.json、local-feature.cadoryx、local-feature.step/iges/stl，以及三语言 local-window 图片和对应 local-preview 原生视口截图。WPF RenderTargetBitmap 不包含 HwndHost 内容，必须把窗口截图和原生视口截图配对查看，不能把前者的空白区域当作视口未初始化。

恢复冒烟在生产 30 秒定时快照后强制终止并重启，核对局部配方、依赖、拓扑引用与精确几何，验证一个旧精确引用 Stale、两个语义/当前来源引用 Resolved，最终关闭资产归零。最新执行证据见 [ROADMAP](ROADMAP.md)。上一阶段草图鼠标输入路由问题保持独立待复核，本阶段没有修改草图产品输入代码。
