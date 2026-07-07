# C_TOOL Code Wiki

本仓库当前只保留 AutoCAD 2024 版本。

## 技术目标

- AutoCAD：2024
- 插件目标框架：`.NET Framework 4.8`
- Bundle：`C_TOOL_2024.bundle`
- 默认 AutoCAD 路径：`C:\Program Files\Autodesk\AutoCAD 2024`

安装程序项目 `C_TOOL_Setup` 可继续使用 .NET SDK 构建；这不代表插件支持其它 AutoCAD 版本。

## 项目结构

| 路径 | 说明 |
| --- | --- |
| `src/C_toolsPlugin` | 主插件，输出 `C_TOOL_NetFx.dll` |
| `src/C_toolsSysPlugin` | 系统配置插件，输出 `V_YYY_NetFx.dll` |
| `src/C_toolsAaaPlugin` | 图块库插件，输出 `V_AAA_NetFx.dll` |
| `src/C_toolsBbbPlugin` | 设备清单插件，输出 `V_BBB_NetFx.dll` |
| `src/C_toolsDddPlugin` | 标注文字插件，输出 `V_DDD_NetFx.dll` |
| `src/C_toolsQqqPlugin` | 打印插件，输出 `V_QQQ_NetFx.dll` |
| `src/C_toolsShared` | 共享基础库 |
| `src/C_toolsJson` | JSON 支持库 |
| `src/QlPlugin` | `F_QL` 可选扩展 |
| `src/C_toolsSetup` | 安装程序 |

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File .\build-bundles.ps1 -Target 2024 -Configuration Release
```

生成 zip：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-bundles.ps1 -Target 2024 -Configuration Release -CreateZip
```

构建安装程序：

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Configuration Release -SingleFile -IncludeBundles -SyncToRoot
```

预检：

```powershell
powershell -ExecutionPolicy Bypass -File .\preflight.ps1 -Configuration Release
```

## Bundle 输出

```text
C_TOOL_2024.bundle
├─ PackageContents.xml
└─ Contents\Win64\
   ├─ C_TOOL_NetFx.dll
   ├─ V_YYY_NetFx.dll
   ├─ V_AAA_NetFx.dll
   ├─ V_BBB_NetFx.dll
   ├─ V_DDD_NetFx.dll
   ├─ V_QQQ_NetFx.dll
   ├─ C_toolsShared.dll
   ├─ C_toolsJson.dll
   └─ QlPlugin.dll
```

`PackageContents.xml` 由 `src/WriteBundlePackageContents.ps1` 自动生成。

## 发布

```powershell
powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version v1.0.3
```

发布资产：

- `C_TOOL_2024.bundle.zip`
- `C_TOOL_Setup.exe`
