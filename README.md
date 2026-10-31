# cooopy

> Windows 桌面剪贴板片段管理器：复制即入库，用标签把零散文字整理成可检索的素材库。

## 功能

- 自动记录剪贴板文本，可随时暂停或关闭；支持去重时间窗、最大长度、单实例运行、托盘常驻、开机自启。
- 两种浏览模式：笔记列表和气泡卡片；气泡内可直接展开编辑、删除、打标签。
- 多标签整理：默认标签（置顶、红/黄/绿紧急程度）加自定义标签；支持标签筛选、隐藏无标签、按时间/置顶排序。
- 快捷键：显示/隐藏主界面；cooopy 到剪贴板 = 帮你按一次 Ctrl+C 后弹出打标签窗口（有选中取选中，没选中取剪贴板内容，剪贴板为空则留空直接输入；前台是传统 cmd 控制台时跳过按键模拟，避免中断命令）。组合键必须以 Ctrl / Shift / Alt 开头（不支持 Win 键）。
- 搜索：文本和标签名搜索，支持多关键词。
- 数据本地保存：`%LOCALAPPDATA%\cooopy\data.json`，原子写入并每日备份；支持 JSON / Markdown 导出和 JSON 导入。
- 证件号自动打码：按地区码、生日和 MOD 11-2 校验位识别 18 位身份证号（保留前 6 后 4），按 Luhn 校验识别 16–19 位银行卡号（保留前 4 后 4），入库前替换中间数字；可在设置中关闭。
- 私密记录：「私密」是内置标签（排在三个紧急程度之后，眼睛带斜杠的图标），打上后列表里正文显示为 `🔒 ********`；查看、编辑、复制都需通过 Windows Hello（PIN/指纹/人脸）验证，验证后 3 分钟内免重复验证；私密记录默认不参与导出。
- 可选 AI 整理：配置 DeepSeek（或 OpenAI 兼容）API 后，可让 AI 把杂乱文本整理成带标签的记录；不配置不影响本地功能。
- 扁平化中文界面，无边框自绘标题栏，支持高 DPI。

## 下载使用

到 [Releases](https://github.com/Cilurus/cooopy/releases) 下载：

- **cooopy-portable-win-x64.zip**：完整便携版，自带 .NET 运行时，解压后直接运行 `cooopy.exe`；
- **cooopy-lite-win-x64.zip**：精简版，体积小，需要先安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)。

解压后也可以双击 `install.cmd` 做一次轻量安装：复制到 `%LOCALAPPDATA%\Programs\cooopy`，创建桌面和开始菜单快捷方式并启动，不需要管理员权限。

数据只保存在当前用户目录，卸载或删除程序文件夹不会删除你的记录。

## 环境要求（源码构建）

- Windows 10 2004（19041）及以上 / Windows 11 —— 私密记录的验证调用 Windows Hello
- .NET 8 SDK

## 构建

```powershell
dotnet build cooopy/cooopy.csproj -c Release
```

直接运行：

```powershell
dotnet publish cooopy/cooopy.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=None -o cooopy/app
```

也可以使用仓库内脚本：

```powershell
.\cooopy\build.ps1            # 构建到 cooopy/app
.\cooopy\build.ps1 -Install   # 构建并安装到当前用户

.\cooopy\release.ps1          # 生成 dist/ 下的完整便携版和精简版 zip
```

## 发布新版本

推荐用根目录的 `publish.ps1`，一条命令完成「改版本号 → 提交 → 打 tag → 推送」，GitHub Actions 随后自动构建并创建 Release：

```powershell
.\publish.ps1 -Patch              # 0.1.0 -> 0.1.1
.\publish.ps1 -Version 0.2.0      # 指定版本号
.\publish.ps1 -Patch -DryRun      # 只检查，不提交/推送
```

也可以手动打 tag：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

也可以在 Actions 页面手动运行 `release` 工作流；构建产物在 `dist/`。

## 项目结构

```text
cooopy/
  Models/          数据模型
  Services/        剪贴板、快捷键、数据存储、托盘等
  Windows/         标签弹窗、编辑窗口等
  installer/       便携包内的轻量安装脚本
  MainWindow.xaml  主界面
  App.xaml         应用入口
  release.ps1      生成 Release 压缩包
  cooopy.csproj
publish.ps1        一键发布新版本
.github/workflows/release.yml
```

## 数据与隐私

- 所有记录默认只保存在本机，不会自动上传；发布包和仓库中不包含任何用户数据。
- AI 功能仅在主动使用时，把当前对话或待整理内容发送到你配置的 API。
- 剪贴板管理器可能捕获密码、密钥等敏感内容；处理敏感信息前请先暂停记录。

## License

MIT，见 [LICENSE](LICENSE)。
