# 构建与仓库维护

需要 Windows 10 / 11 与 .NET Framework 4.6.2 或更高版本。HTML 设置页使用已安装的 Microsoft Edge WebView2 Runtime。

托管代码由系统 x86 csc 编译。Zig、TagLibSharp、WebView2 SDK 恢复到忽略目录，下载后核对固定 SHA-256；不要求全局安装开发工具。

## 构建和检查

在项目根目录执行：

```powershell
.\scripts\build.ps1
.\scripts\build-native.ps1 -Tests
.\scripts\build.ps1 -Tests
.\build\tests.exe
.\scripts\build-setup.ps1 -SkipBuild
.\scripts\check-repo.ps1
```

安装器自测只使用隔离数据，不安装程序或修改自动启动：

```powershell
$p = Start-Process .\build\BetterDownload-Setup.exe --self-test -WindowStyle Hidden -PassThru
$p.WaitForExit()
$p.ExitCode
Get-Content .\build\setup-tests.txt
```

退出码应为 0。安装器是 `build/BetterDownload-Setup.exe`，双击后点击“安装 / 修复”。命令行安装使用 `--install`。

单独复测退出流程可运行 `build/tests.exe --shutdown`；测试只关闭自己创建的宿主和浏览器，不操作 QQ 音乐。HTML 由公开 WebView2 控制器承载，程序自行管理其创建与关闭。

QQ 音乐正在运行时，新版本会先暂存，客户端退出后切换。不要强制替换 QQ 音乐正在使用的 DLL，也不要用编译成功代替真实应用验收。

## 更新文档图片

```powershell
.\build\qqm-cli.exe --readme-cards .\docs\images
.\build\qqm-cli.exe --card-preview .\docs\card-preview.png
.\build\qqm-cli.exe --ui-smoke .\docs\images\settings.png
```

卡片使用真实 WPF 控件；设置图由实际 WebView2 控件加载内置 HTML 后捕获。全部使用虚构歌曲与示例状态。

品牌封面沿用网易云版最新的 HTML/SVG 方案。安装 Node.js、Playwright 并确保 Microsoft Edge 可用后执行：

```powershell
npm install --no-save --package-lock=false playwright
node .\scripts\render-docs.cjs
```

输出 `docs/images/cover.jpg`，源码是 `promo/cover.html`。无远程图片、字体或生成式图片依赖。

## 生成预览包

```powershell
.\scripts\package-preview.ps1
```

包包含安装器、程序、对应源码、许可证和文档，输出到 `dist/`。打包前检查 Git 候选清单，只复制检查通过的源文件，不递归夹带忽略文件。预览使用 `Preview-Cards.cmd`，不会启动转换队列或保存设置。

## 提交前检查

```powershell
.\scripts\check-repo.ps1
git status --short
git diff --cached --stat
```

根目录采用白名单；研究、账号配置、歌曲、密钥、事件、工具链与构建产物不提交。检查脚本核对路径和类型、文档引用、常见私密数据标记，以及关键忽略规则。

唯一纳入仓库的音频是同作者项目的原创一秒测试音 `tests/fixtures/tone.flac`，其 SHA-256 被固定校验。发布前仍需人工查看实际提交清单。
