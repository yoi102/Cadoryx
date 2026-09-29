# Cadoryx 许可与测试签名

Cadoryx 自有源码使用仓库根目录的 [MIT License](../LICENSE)，著作权标记为 `Copyright (c) 2026 yoiri`。MIT 许可允许使用、修改和分发，但分发副本必须保留版权与许可声明。随安装包提供的 OcctSharp/OCCT、MathNet 及其他依赖保留各自的许可；不能把第三方组件描述成由 Cadoryx 重新以 MIT 授权。自包含安装包的 `LICENSE` 与 `licenses/` 目录需随发布产物一起核对。

`scripts/new-test-signing-certificate.ps1` 在当前 Windows 用户的个人证书存储中创建或复用 `CN=yoiri` 自签名代码签名证书。私钥标记为不可导出，脚本不把它加入受信任根证书存储。可将返回的 thumbprint 传给 `scripts/build-installer.ps1 -SigningThumbprint <thumbprint>`，它会在写入 MSI 哈希清单之前签名。`scripts/verify-installer.ps1` 检查签名证书与清单一致；这只能证明本机测试包的签名身份，不表示其他 Windows 设备默认信任该证书。

公开分发须另行取得受信任的代码签名证书或服务，再对最终包执行签名、时间戳与设备验证。用户已明确要求当前不公开发布，所以本地构建和 VM 验收不得上传 MSI 或执行商店/网站的发布操作。
