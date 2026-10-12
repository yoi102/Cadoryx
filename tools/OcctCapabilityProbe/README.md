# OcctSharp 独立包能力验证

仅引用 `OcctSharp` 与 `OcctSharp.Native.win-x64` NuGet，版本跟随仓库 `OcctSharpVersion`。没有 Cadoryx 项目引用、上游源码链接或自定义 P/Invoke。

从 Cadoryx 根目录运行：

```powershell
./tools/OcctCapabilityProbe/Verify.ps1
```

脚本锁定还原并发布到独立目录，从实际恢复缓存核验 Native nupkg 签名及锁定内容哈希，逐一比较包内 DLL 与实际输出，清理 OCCT 相关环境变量并限制 PATH，再执行几何、历史、交换及制造等能力场景。Preview.85 本轮实际执行 55 项，全部通过；场景数及逐项结果以 `evidence/result.json` 为准。真实渐变与移动形体另由 Cadoryx 窗口冒烟检验。运行时还检查已加载的同名原生依赖全部来自该发布目录。`package-audit.json` 记录包身份及载荷哈希；文件样本保留在 evidence 目录供复核。

仓库只配置 nuget.org，版本跟随共享的精确锁定值；前提是本机安装 .NET 10 SDK/运行时环境。Cadoryx 不需要本地 OcctSharp 源码、OCCT 开发 SDK 或 vcpkg。`OcctSharp.Native.win-x64` 继续受支持，负责随 NuGet 提供 Windows x64 原生 DLL，并未因上游增加 vcpkg 构建入口而弃用。失败返回非零退出码，不以已有源码代替包内能力证明。

这组检查回答当前 Cadoryx 能否使用这些公开接口，不是所有 OCCT 方法迁移完毕、真实大型模型或全新机器的验收。范围和应用待接入项见 [能力核查](../../docs/OCCT_CAPABILITY_AUDIT.md)。
