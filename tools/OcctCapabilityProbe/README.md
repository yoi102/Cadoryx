# OcctSharp 独立包能力验证

仅引用 `OcctSharp` 与 `OcctSharp.Native.win-x64` NuGet，版本跟随仓库 `OcctSharpVersion`。没有 Cadoryx 项目引用、上游源码链接或自定义 P/Invoke。

从 Cadoryx 根目录运行：

```powershell
./tools/OcctCapabilityProbe/Verify.ps1
```

脚本锁定还原并发布到独立目录，逐一比较 Native nupkg 内 DLL 与实际输出，清理 OCCT 相关环境变量并限制 PATH，然后执行 18 组场景：原 16 组几何／历史／交换、Viewer 双端渐变公开 API 契约，以及 M2-V5 轮廓面拉伸／旋转及体积检查。真实渐变与移动形体另由 Cadoryx 窗口冒烟检验。运行时还检查已加载的同名原生依赖全部来自该发布目录。`package-audit.json` 记录包身份及载荷哈希，`evidence/result.json` 记录逐场景结果和程序集来源；文件样本保留在 evidence 目录供复核。

前提是本地 NuGet 源存在固定版本族和本机安装 .NET 10 Desktop/运行时环境。普通终端直接运行；受限沙箱可能无法读取用户级 NuGet.Config，需要允许所需访问。失败返回非零退出码，不以已有源码代替包内能力证明。

这组检查回答当前 Cadoryx 能否使用这些公开接口，不是所有 OCCT 方法迁移完毕、真实大型模型或全新机器的验收。范围和应用待接入项见 [能力核查](../../docs/OCCT_CAPABILITY_AUDIT.md)。
