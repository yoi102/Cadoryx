# 固定交换源样本

这些小样本用于 M1-Q。普通测试直接读取本目录中的 STEP/IGES，不在运行时调用 Cadoryx 导出器生成输入。

## 来源与复现

本项目自行构造的解析几何，无第三方模型。维护工具 `tools/ExchangeFixtures` 直接消费 OcctSharp 8.0.1-preview.26，不引用 Cadoryx 项目。OCCT 先生成毫米 BRep/装配和交换语法，再对源文本修改单位声明及面样式。它们是固定的合成源样本，**不能作为独立 CAD 厂商互操作认证**。

```powershell
dotnet run --project tools/ExchangeFixtures -c Release -- Cadoryx.Tests/Fixtures/Exchange
```

仅在有意更新样本时运行；检查文本差异、单位声明、几何及截图。生成器会重写本目录的八个交换文件，日期头可能变化。测试不依赖实体编号或面遍历顺序；生成器对自己的 Box 输出使用固定面顺序，生成后以 STEP 中的 ADVANCED_FACE/PLANE 和实际样式验证。

## 几何预期

| 样本 | 数值坐标及源单位 | 导入后的预期 |
|---|---|---|
| unit-mm.step / .iges | 1 × 2 × 3 mm；生成基线 | 尺寸 1 × 2 × 3 mm |
| unit-inch.step / .iges | 相同的 1 × 2 × 3，单位为 inch | 尺寸 25.4 × 50.8 × 76.2 mm |
| unit-meter.step / .iges | 相同的 1 × 2 × 3，单位为 m | 尺寸 1000 × 2000 × 3000 mm |
| rotated-colors.step / .iges | 10 × 20 × 30 mm，重复旋转装配 | 世界总边界 (-35, 5, -10) 至 (100, 200, 0) mm |

单位 Box 的整体颜色为红色；面积为 `22 × s²` mm²，STEP 实体体积为 `6 × s³` mm³，s 为 1、25.4 或 1000。IGES 基线是六个面的集合，Cadoryx 将其标为 Sheet、实体体积为 0；不能用外包盒代替实体体积。

STEP 英寸使用 CONVERSION_BASED_UNIT + LENGTH_MEASURE_WITH_UNIT(25.4, millimeter)，米使用 SI_UNIT($,.METRE.)。IGES Global 的单位标志/名称分别为 1/IN、6/M，模型缩放保持 1。

旋转装配：共享 Box 的局部位置为 T(5,0,0) × Ry(90°)，其父装配有两份实例：T(100,0,0) × Rz(90°) 和 T(0,200,0) × Rz(180°)。预期零件原点为 (100,5,0)、(-5,200,0)，共享定义但路径不同。STEP 额外对 +Z 面设绿色、-Y 面设蓝色，整体红色。IGES 文件记录生成器的整体红色装配，未添加 STEP 的独立面样式；目前该 IGES 文件保留供手工比对，自动旋转回归以固定 STEP 为输入并测试两种导出。

`ExchangeFixtureTests` 验证单位、面积/体积边界、源颜色、共享定义、旋转、STEP/IGES 导出位置与单位、MessagePack 外观字段兼容及原生保存。`WindowSmokeRunner` 通过正反两个视角验证真正绘制出的红/绿/蓝面，避免把背面不可见误判为样式丢失。
