# 第三方与参考项目

- **NCM-BetterDownload**：同作者项目，GPL-3.0。QQ 版卡片依据其 `plugin/progress-card.js` 重新实现为 WPF 控件；HTML 设置页和文案沿用 `plugin/main.js`，封面沿用 `promo/preview.html` 的程序化设计；`tests/fixtures/tone.flac` 沿用其原创测试音频。项目许可见 `LICENSE`。
- **Microsoft.Web.WebView2 1.0.3537.50**：Microsoft Corporation，BSD-3-Clause。用于承载内置 HTML 设置页，许可见 `licenses/WebView2.txt`。[NuGet](https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3537.50)。运行时由本机 WebView2 Runtime 提供，不包含 QQ 音乐代码。
- **QM Unlock**：MIT，Copyright (c) 2026 QM Unlock contributors。`src/Qmc.cs` 参考其纯 QMC2 实现，未采用登录信息提取或联网获取密钥功能。参考提交 `f9c07cb64a2d586bf0aac47ba10d12fd8712beb5`。[源码](https://github.com/mary20050520/qmunlock/tree/f9c07cb64a2d586bf0aac47ba10d12fd8712beb5)，许可见 `licenses/QMUnlock.txt`。
- **MMKV**：BSD-3-Clause，Copyright (C) 2018 THL A29 Limited, a Tencent company。独立读取器根据公开存储格式实现，没有链接 MMKV 二进制。参考提交 `ad7657ef9d120dbcdd7432d75aa6c59391149b22`。[源码](https://github.com/Tencent/MMKV/tree/ad7657ef9d120dbcdd7432d75aa6c59391149b22)，许可见 `licenses/MMKV.txt`。
- **TagLibSharp 2.3.0**：LGPL-2.1，独立 DLL 动态链接，许可见 `licenses/TagLibSharp.txt`。[源码与构建说明](https://github.com/mono/taglib-sharp/tree/TaglibSharp-2.3.0.0)，[NuGet](https://www.nuget.org/packages/TagLibSharp/2.3.0)。允许修改、重新构建与调试该库。可用接口兼容的 DLL 替换构建目录中的依赖，再执行 `scripts/build-setup.ps1 -SkipBuild` 生成匹配的新安装清单；安装后的文件完整性校验会拒绝未经重新打包的替换文件。
- **BetterNCM-Installer**：作为版本选择逻辑的研究参考，未复制其安装器代码。[源码](https://github.com/std-microblock/BetterNCM-Installer)。

Windows/.NET 系统库、QQ 音乐客户端 DLL、用户歌曲和本机密钥均不包含在预览包中。
