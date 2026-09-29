# 打开文件的进度与取消

`.cadoryx` 的 `MainWindowViewModel.OpenPathAsync` 使用 MaterialDesign `DialogHost` 显示 `ProgressDialog`。STEP/IGES/STL 已改为右下角非模态通知，提供取消按钮，可在应用设置中选择按主窗口或桌面定位，详见 [消息通知](NOTIFICATIONS.md)。读取成功且未取消才创建文档；取消不记录为错误。`.cadoryx` 重复打开直接激活已有文档。

## 调用方式

推荐通过注入的 `IDialogService` 托管整个操作：

```csharp
await dialogs.RunWithProgressAsync(
    token => ReadAsync(token),
    canCancel: true,
    message: "正在打开文件…");

// 默认不显示取消按钮，operation 收到 CancellationToken.None。
await dialogs.RunWithProgressAsync(_ => WorkAsync());
```

调用方必须处理传入的 token。按钮只请求取消，不会提前关闭弹窗；重复取消被禁用。操作成功、失败或取消后，服务都关闭自己拥有的弹窗。`OperationCanceledException` 交由调用方区分处理，主窗口将其显示为“已取消”。

需要自己管理生命周期时：

```csharp
using var cancellation = new CancellationTokenSource();
using var progress = dialogs.ShowProgressBarDialog(
    showCancelButton: true,
    cancel: cancellation.Cancel,
    message: "正在读取…");
await ReadAsync(cancellation.Token);
```

`ShowProgressBarDialog()` 默认隐藏取消按钮；设置 `showCancelButton: true` 必须提供回调。直接使用控件时可绑定 `ShowCancelButton`、`CancelCommand`、`Message` 三个依赖属性。文本通过已有三语言资源提供。

运行中的弹窗由 scope 持有，`Close`、遮罩或退出窗口不能提前结束它；应先取消并等待操作退出，或由调用者结束操作后释放 scope。已有其他弹窗时拒绝覆盖；旧 scope 的释放不会关闭后来的弹窗。

## STEP 取消速度

仅传递 `CancellationToken` 无法打断当前 OcctSharp 的 `XdeDocument.ReadExchange`。上游 OCCT 8.0.1 的 `STEPCAFControl_Reader.ReadFile` 本身没有 progress/cancel 参数，因而只新增 C# 参数不能解决解析阶段的等待。本次保持锁定 NuGet 不变。

桌面 `IExchangeImportService` 由 `IsolatedExchangeImporter` 实现：

1. 启动同一发布目录下的 `Cadoryx.wpf.exe --import-worker`，不初始化 WPF Application、设置、恢复服务和主窗口。
2. 读取进程独占本次任务的临时目录；其 `TEMP`/`TMP` 也指向该目录。通过 Windows Job Object 管理生命周期。
3. 点击右下角通知中的取消按钮，结束等待并关闭 Job，终止正在执行原生解析或转换的读取进程；等待进程退出，再清理临时数据。
4. 成功时通过现有 `.cadoryx` 容器交回完整快照、BRep/XDE 资产及导入诊断。主进程读取后再次检查取消状态，再提交到工作区。

STEP、IGES、STL 的桌面打开共用该流程。`.cadoryx` 使用存储层的协作取消；CLI 和内部内核调用保持原流程。只结束本次创建的读取进程，不结束主窗口或其他进程。Job 已关联时，主窗口异常退出也会结束读取进程。

临时容器会增加成功导入的磁盘读写和短时磁盘占用。正常完成、取消和失败都会尝试清理本次私有目录；主进程崩溃或文件被外部程序占用时可能留下临时目录，尚未实现启动时清理遗留目录。真实大装配导入性能需单独测量，不能用取消合成样本替代。

## 验证入口

`scripts/verify.ps1 -PublishSmoke -WindowSmoke` 的发布版桌面冒烟包含：

- `ProgressDialogSmokeRunner`：默认隐藏/按需显示、取消只调用一次、持有资产期间取消及释放、禁止提前关闭、成功/失败关闭、旧 scope 所有权、原生文档打开取消/失败/重复激活。
- `ExchangeImportSmokeRunner`：生成大量合法 STEP 点实体，等源文件确实被原生读取器打开且 CPU 继续执行后，通过进度按钮命令取消，记录从取消到工作区空闲的时间；确认进程退出、临时目录清理、源文件 SHA256 不变、文档和资产不变。随后验证坏 STEP 错误处理、IGES/STL 以及桌面 STEP 成功导入。
- `WindowSmokeRunner`：继续覆盖交换样本的单位、旋转、颜色和真实视口。

计时和结果见对应 `artifacts/smoke-*/exchange-cancel-result.txt`，进度弹窗结果见 `progress-dialog-result.txt` 和三个 `progress-*.png`。这是一台机器上的合成解析工作负载，不承诺所有磁盘/文件规模下的固定取消耗时。

2026-09-26 历史结果（当时交换格式也使用模态进度弹窗；当前通知版验证见 [消息通知](NOTIFICATIONS.md)）：

- Release 发布成功，无编译警告/错误；`dotnet test Cadoryx.Tests/Cadoryx.Tests.csproj -c Release --no-restore` 为 **521/521**，日志 `artifacts/progress-unit-tests.log`。
- 发布版桌面最终证据 `artifacts/smoke-20260926-235335`：82,907,458 字节合成 STEP，原生解析中取消到空闲 **23 ms**；进程、临时目录、文档/资产、源文件校验、错误恢复及 STEP/IGES/STL 后续导入均通过。
- 发布版窗口证据 `artifacts/window-smoke-20260926-234957`：交换文件、源/面颜色、旧文件迁移、视口和窗口生命周期通过。该轮之后仅调整取消测试数据的实体编号，并重新运行桌面冒烟；生产导入代码未改变。
- 两组 WPF 绑定日志均为 0 字节；三个语言资源均为 514 个非空键。已检查进度弹窗截图和 `git diff --check`。
- 本轮未执行恢复强杀/草图指针专项，未验收真实厂商大型模型、网络盘或异常关机后的临时目录回收。
