# 安装 ZCompare

[English](INSTALL.md)

## 方式 A：安装包（推荐）

1. 打开官方 [ZCompare Releases](https://github.com/Zcube7/ZCompare/releases) 页面。
2. 下载 `ZCompare-0.1.1-win-x64-setup.exe` 和 `SHA256SUMS.txt`。
3. 校验 SHA-256：

   ```powershell
   Get-FileHash .\ZCompare-0.1.1-win-x64-setup.exe -Algorithm SHA256
   Get-Content .\SHA256SUMS.txt
   ```

4. 运行安装包。程序会安装到当前用户的 `%LocalAppData%\Programs\ZCompare`，不需要管理员权限，也不需要另装 .NET。

v0.1.1 暂未购买代码签名，因此 SmartScreen 可能提示“无法识别的应用”。只有在确认来源是官方仓库且 SHA-256 一致后，才按 **“更多信息”→“仍要运行”**；不要关闭 SmartScreen。

以后版本使用同一个 AppId，会原位覆盖升级。卸载程序默认不会删除 `%LocalAppData%\ZCompare` 中的最近记录和命名配置。

## 方式 B：便携开发者包

1. 安装 [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)。
2. 下载并校验 `ZCompare-0.1.1-win-x64-portable-fdd.zip`。
3. 解压到一个新文件夹。
4. 运行 `ZCompare.App.exe`；需要 CLI 时，在该目录打开终端并运行 `zcompare.exe --help`。

不要直接在 ZIP 压缩包内部运行程序。

## 方式 C：从源码构建

安装 Git 和 .NET 10 SDK，然后执行：

```powershell
git clone https://github.com/Zcube7/ZCompare.git
cd ZCompare
dotnet restore ZCompare.slnx
dotnet build ZCompare.slnx -c Release --no-restore
$env:WINDIR = $env:SystemRoot
dotnet test ZCompare.slnx -c Release --no-build
dotnet run --project src/ZCompare.App/ZCompare.App.csproj
```

## 方式 D：让 AI 辅助安装

可以把下面提示词交给本机 AI 助手。任何命令执行前都应由你阅读并确认。

> 请为 Windows x64 安装 ZCompare。只能从 GitHub 官方仓库 `Zcube7/ZCompare` 的 Release 下载；同时下载 `SHA256SUMS.txt`，对选中的文件做 SHA-256 校验，并在运行任何程序前向我展示校验结果。不得绕过或关闭 Windows 安全保护；未经我明确同意不得运行安装包；不得上传或泄露任何 XLSX、路径、对比报告、配置、用户名或公司数据。

## 更新

ZCompare 最多每 24 小时在后台查询一次 GitHub 最新稳定 Release。发现新版时，提示条会在浏览器中打开官方安装包地址。请校验新版 SHA-256、关闭 ZCompare，再运行安装包覆盖升级。程序不会静默下载或执行更新。

## 网络与隐私

程序不会把工作簿、路径、报告或设置发送到服务器。更新检查只访问 `api.github.com`，发送标准 HTTP 头和已安装版本。最近路径与更新缓存保存在 `%LocalAppData%\ZCompare`。v0.1.1 不生成诊断日志文件。
