# C_TOOL

`C_TOOL` 是一套面向 AutoCAD 2024 的 Windows 插件工作区，主要使用 C#/.NET 开发，并通过 Autodesk Application Bundle 方式打包、分发和安装。

当前仓库只保留 AutoCAD 2024 支持：

- `C_TOOL_2024.bundle`
  面向 AutoCAD 2024，插件运行时基于 `.NET Framework 4.8`

## 文档入口

- [C_TOOL/README.md](./C_TOOL/README.md)
- [C_TOOL/RELEASE.md](./C_TOOL/RELEASE.md)
- [C_TOOL/RUNNER.md](./C_TOOL/RUNNER.md)
- [CHANGELOG.md](./CHANGELOG.md)

## 主要目录

- `src/`
  主解决方案源码目录
- `C_TOOL/`
  仓库说明、发布、Runner、测试文档
- `C_TOOL_2024.bundle/`
  AutoCAD 2024 bundle 输出目录
- `Bundles/`
  安装程序扫描的 bundle 仓库目录

`F_QL` 是 `V_AAA` 内的可选桥接命令，其源码在 `src/QlPlugin`。构建脚本会按需编译 `QlPlugin.dll` 并同步到 `C_TOOL_2024.bundle`。

## 快速构建

在仓库根目录执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-bundles.ps1 -Target 2024 -Configuration Release
```

如果需要生成 zip：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-bundles.ps1 -Target 2024 -Configuration Release -CreateZip
```

如果需要构建安装程序：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Configuration Release -SingleFile -IncludeBundles -SyncToRoot
```

发布或提交前建议先跑预检：

```powershell
powershell -ExecutionPolicy Bypass -File .\preflight.ps1 -Configuration Release
```

## 发布资产

当前发布只包含 AutoCAD 2024 资产：

- `C_TOOL_2024.bundle.zip`
- `C_TOOL_Setup.exe`

示例：

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version v1.0.3
```

## 在线更新发布

发布脚本会生成安装器、bundle zip 和 `latest.json`。如果服务器目录按下面放置：

```text
/c-tool/
  latest.json
  releases/
    v1.0.4/
      C_TOOL_2024.bundle.zip
      C_TOOL_Setup.exe
```

可以这样生成带相对下载地址的更新清单：

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version v1.0.4 -SkipGitHubRelease -SkipTag
```

如果想让 `latest.json` 直接写完整下载地址：

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version v1.0.4 -SkipGitHubRelease -SkipTag -UpdateBaseUrl "https://example.com/c-tool/releases/v1.0.4"
```

安装器检查更新时会读取环境变量 `C_TOOL_UPDATE_MANIFEST_URL`，或注册表 `HKCU\Software\C_TOOL\UpdateManifestUrl`。值应指向服务器上的 `latest.json`，例如：

```powershell
setx C_TOOL_UPDATE_MANIFEST_URL "https://example.com/c-tool/latest.json"
```

没有服务器时，也可以把更新文件放在内网共享目录：

```text
\\192.168.1.10\C_TOOL_Update\
  latest.json
  releases\
    v1.0.4\
      C_TOOL_2024.bundle.zip
      C_TOOL_Setup.exe
```

这种方式下可以直接使用不带 `-UpdateBaseUrl` 的清单，`latest.json` 里的相对路径会自动按共享目录解析：

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version v1.0.4 -SkipGitHubRelease -SkipTag
```

用户电脑配置共享目录清单路径：

```powershell
setx C_TOOL_UPDATE_MANIFEST_URL "\\192.168.1.10\C_TOOL_Update\latest.json"
```

也支持本机路径或 `file:///` 路径，例如：

```powershell
setx C_TOOL_UPDATE_MANIFEST_URL "D:\C_TOOL_Update\latest.json"
```
