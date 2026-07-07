# C_toolsTemplatePlugin

这是一个面向 AutoCAD 2024 `.NET Framework 4.8` 的最小插件模板工程，适合作为新命令或新插件模块的起点。

## 模板内容

- `TemplatePluginApp.cs`
  - `IExtensionApplication` 入口，复用共享基类统一处理生命周期与全局异常兜底。
- `TemplatePluginCommandIds.cs`
  - 集中管理命令组和命令名，便于后续统一改名。
- `TemplateCommandExecutor.cs`
  - 封装活动文档获取、只读事务、写事务、可选文档锁和异常处理。
- `TemplateCommands.cs`
  - `CTPLHELLO`：验证插件是否成功加载。
  - `CTPLCOUNTLINES`：演示只读事务和类型安全读取。
  - `CTPLADDCIRCLE`：演示写事务、实体创建和资源释放。

## 设计要点

- 事务边界集中在 `TemplateCommandExecutor` 中，避免命令方法里散落重复样板代码。
- 读取对象使用 `OpenAs<T>` 做类型校验，实体类型不匹配时立即抛出明确异常。
- 新建实体使用 `using` 管理包装对象生命周期，并交由事务注册到数据库。
- AutoCAD 用户取消和普通异常分开处理，避免把取消操作误报为错误。

## 改造成正式插件时建议做的事

1. 重命名命名空间、程序集名和命令名。
2. 将示例命令替换成你的业务命令或服务类。
3. 如果命令会从模型窗体、托盘面板或 `Session` 上下文写图，请把 `ExecuteWrite(..., requireDocumentLock: true)` 打开。
4. 如果要随 bundle 自动加载，再把输出路径改到目标 `.bundle`，并补充 `PackageContents.xml` 的 `ComponentEntry`。
