# 构建与仓库维护

需要 Windows 10 / 11 与 .NET Framework 4.6.2 或更高版本。正式接入使用 QQ 音乐自带的原生 HTML 控件；独立设计预览和浏览器测试使用 Microsoft Edge WebView2 Runtime。

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

退出码应为 0。安装器是 `build/BetterDownload-Setup.exe`，单文件即可分发：程序、接入 DLL、依赖和许可证都以资源形式内嵌。双击后点击大按钮（安装 / 更新到 vX / 修复），完成后点击“完成”关闭。命令行安装使用 `--install`，静默卸载使用 `--uninstall`（默认保留设置和转换记录，加 `--purge` 同时删除插件数据，歌曲始终保留）。安装器没有打开设置的参数或按钮：设置只在 QQ 音乐内打开。

安装器声明 DPI 感知，按系统缩放自行布局，并读取 Windows 的应用浅色 / 深色设置。它与程序共用 `src/ClientCompatibility.cs` 判断 QQ 音乐的适配情况，窗口打开后在后台检查，结果与程序一致。

真实安装需要从 QQ 音乐可见的用户目录运行。部分打包桌面应用启动的子进程会继承 AppData 文件重定向；此时安装器会提示在资源管理器中直接打开安装包。哈希校验只能确认内容，不能证明另一个进程能访问同一路径。安装器因此会创建临时探测文件，用文件句柄核对实际位置，检查完成后自动移除探测文件。

单独复测独立预览的退出流程可运行 `build/tests.exe --shutdown`；测试只关闭自己创建的浏览器，不操作 QQ 音乐。该模式由公开 WebView2 控制器承载，与进程内 GF 控件分开。

`build/tests.exe --lifecycle` 检查主窗口识别、最小化与工作进程退出；`--local-page` 检查本地页面通信；`--bridge` 检查文件事件和预览退出。GF 原生界面需要使用受支持的真实客户端验收。

`build/tests.exe --compat` 检查跨版本识别、离线签名校验、密钥接口的子进程隔离和独立设置窗口。`build-native.ps1 -Tests` 会从 `tests/FakeKeys.c` 编出四个未签名的替身接口（正常、格式错误、崩溃、卡死），只供测试使用。在装有 QQ 音乐的电脑上，可以运行 `build/tests.exe --real-client <QQ 音乐安装目录>` 核对真实组件；它只输出检查结果和下载记录数量，不输出密钥和文件名。

`native/gf_ui.c` 在 QQ 音乐 UI 线程创建 GF 元素，`NativeUiSession` 核对组件指纹后才启用该接口。HTML 的通信服务只绑定随机本机端口，使用每次会话独立的地址令牌，并校验 Host、Origin 和动作类型。没有远程页面、目录浏览或脚本执行接口。

“检查更新”是程序唯一的联网操作：只在用户点击设置页右上角的按钮时，由 `src/UpdateCheck.cs` 请求 GitHub Releases API，页面的内容安全策略仍只允许连接本机服务。发现新版本时只打开本仓库的发布页，不下载或运行任何文件。`build/tests.exe --update` 离线检查结果解析。

QQ 音乐正在运行时，新版本会先暂存，客户端退出后切换。不要强制替换 QQ 音乐正在使用的 DLL，也不要用编译成功代替真实应用验收。

## 更新文档图片与 LOGO

```powershell
.\build\qqm-cli.exe --readme-cards .\docs\images
.\build\qqm-cli.exe --ui-smoke .\docs\images\settings.png
```

卡片使用真实 WPF 控件；设置图由实际 WebView2 控件加载内置 HTML 后捕获。加 `--intro` 则捕获首次打开时的介绍与免责声明页（如 `--ui-smoke .\build\intro.png --intro`），不加时截图跳过该页。全部使用虚构歌曲与示例状态。

安装器窗口用它自己的绘制代码渲染，不安装、也不读取或改动已有安装：

```powershell
Start-Process .\build\BetterDownload-Setup.exe -ArgumentList '--ui-preview', "$PWD\build\setup-preview" -Wait
```

输出安装、进行中、完成、已安装、更新、卸载确认、卸载确认（勾选删除数据）、已卸载、失败和界面未适配 10 种状态的浅色 / 深色 2 倍图，以及 README 使用的 `setup.png`（浅色首次安装与深色安装完成并排）。更新配图时把它复制到 `docs/images/setup.png`。本地核对全部六种卡片状态可运行 `.\build\qqm-cli.exe --card-preview .\build\card-preview.png`；只看交互效果用 `.\build\BetterDownload.exe --card-demo`，不读取密钥、不处理歌曲、不保存设置。

品牌封面沿用网易云版最新的 HTML/SVG 方案。安装 Node.js、Playwright 并确保 Microsoft Edge 可用后执行：

```powershell
npm install --no-save --package-lock=false playwright
node .\scripts\render-docs.cjs
node .\scripts\render-icon.cjs
```

输出 `docs/images/cover.jpg` 和 `src/BetterDownload.ico`，源码都是 `promo/cover.html` 中的同一个品牌矢量。无远程图片、字体或生成式图片依赖。

LOGO：连续圆角方块，填充取自 QQ 音乐标志的青绿渐变 `#14D6C0 → #0CC48F → #06BA6C`；白色下载箭头，解锁锁头为 QQ 音乐标志的黄色 `#FFDC00`。修改 LOGO 时同步更新 `src/ui/settings.html` 中 `.nbd-logo` 的渐变和内嵌 SVG，再重新生成封面、ICO 和设置页截图。

ICO 用于应用、安装器窗口和任务栏，小尺寸帧单独加粗放大；安装器还把它作为资源读取，在窗口中显示 LOGO。进度卡片等界面强调色使用 QQ 音乐主题绿 `#1ECC94`，与 LOGO 渐变区分。安装器采用苹果系统配色：浅色 `#F5F5F7` / 深色 `#1C1C1E` 底，分组列表为白色 / `#2C2C2E`；大按钮用加深的品牌绿 `#10A87A`，保证白字清晰，完成标记仍用 `#1ECC94`。“设置入口”一行的图标按 `src/ui/entry.svg` 的同一组路径绘制。QQ 顶栏设置入口图标使用 `src/ui/entry.svg`，单色线性、无底色；`NativeUiSession` 在运行时把描边色换成客户端主题绿，得到悬停 / 按下状态。

## 生成预览包

```powershell
.\scripts\package-preview.ps1
```

面向用户只需发布 `dist/BetterDownload-Setup.exe`。预览包另含程序、对应源码、许可证和文档，输出到 `dist/`。打包前检查 Git 候选清单，只复制检查通过的源文件，不递归夹带忽略文件。预览使用 `Preview-Cards.cmd`，不会启动转换队列或保存设置。

打包直接写入 ZIP，不创建展开的暂存副本；另输出 `dist/BetterDownload-Setup.exe` 方便本机安装。

## 提交前检查

```powershell
.\scripts\check-repo.ps1
git status --short
git diff --cached --stat
```

根目录采用白名单；研究、账号配置、歌曲、密钥、事件、工具链与构建产物不提交。检查脚本核对路径和类型、文档引用、常见私密数据标记，以及关键忽略规则。

唯一纳入仓库的音频是同作者项目的原创一秒测试音 `tests/fixtures/tone.flac`，其 SHA-256 被固定校验。发布前仍需人工查看实际提交清单。
