# 专注花园 · Focus Garden

Windows 桌面番茄钟 MVP 0.2，采用 C# / .NET 8 WPF、SQLite 与 Edge/Chrome Manifest V3 扩展。无需账户，专注记录只保存在本机。0.2 统一了深色背景与浅色文字，将年度热力图放到番茄钟主页，并支持自选专注背景图片和当前日期时间显示。

## 使用便携版

完整解压 Windows x64 便携包，双击文件夹内的 `FocusGarden.exe`。程序已包含运行环境，无需另装 .NET。请保留同文件夹内的 DLL 与资源；不要只复制 EXE。

如先前的验收窗口或旧版仍开着，请先正常关闭，再打开便携版。程序每个 Windows 登录会话只运行一个实例。升级时将 0.2 完整解压到新文件夹，启动后确认窗口标题带有 `0.2`，避免继续使用旧程序。原有 `%LOCALAPPDATA%\FocusGarden` 数据保持兼容，不需要删除或重建数据库。

1. 填写任务和时长。勾选“全屏沉浸”显示全屏倒计时；取消勾选则使用工作模式。
2. 在“使用限制”中填写进程名与域名并保存。应用限制在本轮专注期间将匹配的前台窗口最小化，不结束进程。
3. 网站限制需要自行加载 `browser-extension` 文件夹并粘贴配对码。未连接扩展时，桌面计时仍可用，但网站不会被限制。
4. 紧急退出按钮或已成功注册的 `Ctrl + Alt + Esc` 可结束专注。全局快捷键被占用时，使用可见按钮。
5. 在番茄钟主页查看年度热力图，并用左右箭头切换年份；在“专注统计”查看当天累计时长、完成番茄数和最近记录，也可导出 CSV。
6. 在主页点击“选择背景图片”，选择不超过 25 MB 的 JPG/JPEG、PNG 或 BMP 图片。图片同步用于主页倒计时区域和全屏专注页；“恢复纯色”可取消背景。专注或休息进行中不能更换图片。

主页和全屏专注页都会显示本机当前日期与 `HH:mm:ss` 时间，与本轮倒计时分开标注。所选图片复制到本机数据目录的 `backgrounds` 文件夹，选择保存在 `appearance.json`；程序不修改原始图片，重启后会恢复该选择。

详细操作见 [使用说明](docs/使用说明.md)，实现与边界见 [技术方案](docs/技术方案.md)，本次测试证据见 [验收报告](docs/验收报告.md)。

## 当前版本边界

- 这是可解除的专注工具；全屏覆盖一个显示器，并非 Windows 安全锁屏，也无法阻止所有系统级退出。
- 网站仅拦截新页面导航，包含指定域名的子域。已打开页面、正在播放的媒体与其他浏览器不受此规则自动影响。
- 扩展通常约 5 秒同步，后台 alarm 每 30 秒尝试唤醒。浏览器冻结时没有硬性解除时限，可禁用扩展解除。
- 睡眠、系统锁定、或计时回调间隔超过 5 秒将中断本轮；中断期间不补计时长。系统/管理员窗口、后台进程以及改名应用不保证拦截。
- 热力图累计实际记录的专注时间，包括提前结束和异常中断；“完成番茄”只统计完整完成的会话。

## 从源码运行

开发需要 Windows x64、.NET 8 或更新版本 SDK。扩展自动测试需要 Node.js 18 或更新版本。普通权限即可，不需要修改 hosts、注册系统服务或调整系统安全设置。

```powershell
dotnet run --project src/FocusGarden.App/FocusGarden.App.csproj
```

执行自动检查：

```powershell
dotnet run --project tests/FocusGarden.Core.Tests/FocusGarden.Core.Tests.csproj -c Release
dotnet run --project tests/FocusGarden.Timing.Tests/FocusGarden.Timing.Tests.csproj -c Release
dotnet run --project tests/FocusGarden.Bridge.Tests/FocusGarden.Bridge.Tests.csproj -c Release
dotnet run --project tests/FocusGarden.Appearance.Tests/FocusGarden.Appearance.Tests.csproj -c Release
node --test browser-extension/tests/controller.test.js
```

使用 `build.ps1` 构建自带运行时的 Windows x64 便携版。默认输出在源码目录的 `artifacts/FocusGarden-Windows-x64`。

开发人员短时验收可传 `--test-mode --data-dir <独立目录>`，主界面会增加“10 秒验收”。正常启动不显示测试按钮，交付包未预置任何测试记录。

## 后续迭代

先补齐真实 WPF 交互、多显示器、高缩放、真实休眠与扩展完整联调验收，再迭代后台托盘、自动长休息、已打开网页遮罩和更细的规则配置。现有源码、数据库模型与独立扩展均保留，方便继续实现。
