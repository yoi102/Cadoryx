# M17 交付环境取证矩阵

更新：2026-09-29。M13 的 MSI 构建与同机安装卸载通过不代表新机器、签名和公开发布完成。M17 的结论以固定 MSI SHA-256、同一组固定样本哈希和每台实际机器的原始日志为准。

在目标 Windows x64 机器复制整个安装器目录（MSI 与 `manifest.json`），运行：

```powershell
pwsh -NoProfile -File scripts/collect-m17-environment.ps1 -Installer C:\path\Cadoryx-0.4.12-win-x64.msi -Samples C:\path\11WiFi.step -RunInstallSmoke
```

该脚本先核对 MSI 清单哈希，再记录系统、CPU、GPU/显示设备、运行环境、签名状态和样本哈希；指定 `-RunInstallSmoke` 才调用已有的实际安装、安装目录桌面冒烟与卸载流程。输出目录保留 `environment.json`、安装日志和桌面日志。签名状态只记录实测值，未签名时不得写 PASS。不要将此脚本在开发机运行的结果标为“干净机器”。

长时间交互时先手动打开 Cadoryx、载入固定模型并取得进程 ID，运行：

```powershell
pwsh -NoProfile -File scripts/sample-m17-process.ps1 -ProcessId 12345 -Output C:\evidence\resources.csv -Minutes 120 -IntervalSeconds 30
```

CSV 只记录 CPU、工作集、私有内存、句柄与线程趋势。另附人工操作时间线、模型与文档哈希、卡顿/崩溃现象，不能用空闲采样替代持续交互。

| 门禁 | 所需实际证据 | 当前状态 |
|---|---|---|
| 独立 Windows x64 | 无开发 SDK/本地 NuGet 前提的安装、首次启动、STEP/IGES/STL、保存/恢复、升级/卸载，原始日志与 MSI 哈希 | 未验收 |
| 多显示器与混合 DPI | 两台不同缩放显示器间的主窗、浮动窗、对话框拖动及命中/绘制记录 | 未验收 |
| RDP 与多 GPU | 断线重连、不同显卡/驱动切换后的视口恢复及资源释放 | 未验收 |
| 长时间与真实介质 | 数小时实际建模记录和资源曲线；真实磁盘满、断电、网络盘单列实验 | 未验收 |
| 纸质打印 | 指定打印机型号、驱动、页面比例与纸张实物对照 | 未验收 |
| 签名与公开发布 | 证书身份、签名时间戳与验证、最终可下载包及公开地址 | 未验收 |

安装和长期运行脚本不自动产生设备、证书或外部服务证据；这些门禁由实际环境执行后按原始结果更新。

本机基线（2026-09-29）：`artifacts/installer-20260929-003322/Cadoryx-0.4.12-win-x64.msi` 已构建，SHA-256 为 `63edc2d849a800ab6f94d75fd4af2d5e374a794f3b9e7e7deee5336d63f021d7`。`verification-20260929-003434/result.txt` 报告 579 个已安装文件哈希、快捷方式/卸载注册、安装目录桌面冒烟、零绑定错误、卸载与用户数据保留通过；`m17-environment-20260929-003741/environment.json` 记录同机环境、`11WiFi.step` 样本哈希，以及 MSI 签名状态为 `NotSigned`。这些均为开发机证据，表中各外部环境门禁仍是“未验收”。

后续本机包（2026-09-29）：`artifacts/installer-20260929-010142/Cadoryx-0.4.12-win-x64.msi` 的 SHA-256 为 `04f8f49458ab6a25122e1c80766da4f096e6ffc09e8b25ae70592f88089c8fb4`，包含根目录 MIT `LICENSE` 与第三方许可，共 580 个安装文件。使用当前用户不可导出的 `CN=yoiri` 自签名**测试证书**签名（thumbprint `9D55C023849B9187BE3C6DF9182C76690DC1644D`）；`signtool verify /pa` 明确返回「根证书不受信任」，且无时间戳，因此这不满足公开发行签名门禁。`verification-20260929-010238/result.txt` 报告安装文件哈希、安装目录桌面冒烟、零绑定错误、卸载与用户数据保留通过。完整 Release 构建零警告、600/600 测试，以及 CLI、桌面、窗口、草图、图纸、恢复冒烟也通过，证据目录时间戳 `20260929-005631`。这些都是开发机结果。

**当前最终本地测试包**：在后续抑制依赖修正后重新构建的 `artifacts/installer-20260929-011456/Cadoryx-0.4.12-win-x64.msi`，SHA-256 `126d0c70c78e67ed59d3db1adf941517f2fe770c66416ce38a092c5f57bbaa42`。同一 `CN=yoiri` 测试证书签名，仍不受 Windows 默认信任且无时间戳；MIT `LICENSE` 与第三方许可在 580 个安装文件中。`verification-20260929-011545/result.txt` 报告 580 个安装文件哈希、安装目录桌面冒烟、零绑定错误、卸载和用户数据保留通过。与此包对应的 Release 构建零警告/错误、601/601 测试、CLI 和发布版桌面冒烟通过；此前同轮草图、图纸、窗口和恢复专项证据时间戳为 `20260929-005631`，其后新增的代码仅涉及特征抑制依赖边界，专项 3/3 测试及最终桌面冒烟覆盖该改动。

已定位用户当前运行的 VMware Workstation `Windows 11 x64` 虚拟机；VMware `vmrun` 访问时要求该虚拟机的解锁密码，来宾 RDP/WinRM 端口也未开放。用户随后明确要求暂时不做 VM 验收，因此仍没有在 VM 内运行上述 MSI 的证据，不能将它列为独立机器验收。用户明确要求当前不公开发布；上述 MSI 仅作为本地测试产物保留。
