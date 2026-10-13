# F5：可调用命令目录与专用工具路由

2026-10-13，开发源码1078，OcctSharp仍为公开Preview.85。本次无需新增SDK API。

1077实际工程图第二轮已经创建Top视图，但模型随后尝试非法`DELETE feature-id ...`、用工程图工具删除建模特征，最后把`cad_status.commands`中的`HISTORY SUPPRESS|RESTORE ...`帮助语法直接传给`cad_command`。后者始终拒绝HISTORY及WORKSPACE/WS，要求使用带严格身份和游标的专用工具。因此原目录确实包含该工具无法执行的入口；这处矛盾已修正，但不能据单次模型失败证明唯一根因。

`cad_status.commands`现在只列出`cad_command`实际允许提交的命令，保持其余项目与顺序。新增只读`commandTools`映射：HISTORY→cad_history，WORKSPACE/WS→cad_workspace。目录与原执行拒绝使用同一份精确、忽略大小写的名字映射；没有增加DELETE，没有自动转换请求或填入身份/游标。终端注册、命令补全和HELP继续保留原HISTORY/WORKSPACE语法。

[独立证据](../capability-evidence/diagnostics/af1078-command-catalog/verification.json)验证原1189输入只修改一份生产文件，原6652测试源码保持，新增12项检查。原1076程序集每配置复现4项目录失败，其他8项拒绝守卫通过；1078隔离候选和合入后根每配置各57项编译断言直接调用通过（原45加新12）。新检查覆盖无文档/活动/已分离文档、状态和历史不变、大小写/引号/别名、拒绝前不委派，以及不误过滤名字相近的命令和终端HELP保持。

Tests依赖图和WPF两配置构建零警告/错误；四份输出SDK13/native62磁盘载荷匹配公开85，探针禁止加载原生库并实际观察到零原生模块。原8192上下文、1024输出、普通/激进余量与十二工具检查通过，工程图raw/wire schema逐字节保持。

这是托管行为和构建证据，不是完整xunit/原生回归或供应商验收。预计完整6664尚未全量发现/运行；原模型、性能、D6及材质窗口需等待SDK交还原生时段，绑定最新最终载荷后执行。原24严格STEP失败及A–F其他范围保持，G和清理暂缓。
