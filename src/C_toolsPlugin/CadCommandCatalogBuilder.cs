using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;

namespace C_toolsPlugin;

/// <summary>
/// 从已加载程序集扫描 <see cref="CommandMethodAttribute"/>，并与 PGP 解析结果合并为参考列表。
/// </summary>
internal static class CadCommandCatalogBuilder
{
    private static string? _dotNetScanFingerprint;
    private static Dictionary<string, string>? _dotNetScanCache;

    /// <summary>下次扫描前丢弃反射结果（例如用户点「刷新目录」）。</summary>
    internal static void InvalidateDotNetCommandScanCache()
    {
        _dotNetScanFingerprint = null;
        _dotNetScanCache = null;
    }

    internal const string TagCadNative = "CAD原生命令";
    /// <summary><c>V_*</c> / <c>F_*</c> 以及 C_TOOL 自有入口命令页。</summary>
    internal const string TagVCommand = "V命令";
    /// <summary>其它托管插件与外部插件命令页。</summary>
    internal const string TagPluginCommand = "插件命令";
    internal const string TagVPrefix = TagVCommand;
    internal const string TagOtherPlugin = TagPluginCommand;
    /// <summary>图层别名：由 c_tools_layer_shortcuts.lsp 提供 A1 等（不在此扫描中列出，见「图层命令」页）。</summary>
    internal const string TagLayerShortcut = "图层命令";

    internal static Dictionary<string, string> ScanDotNetCommandMethods()
    {
        var fp = ComputeDotNetScanFingerprint();
        if (_dotNetScanFingerprint == fp && _dotNetScanCache != null)
            return _dotNetScanCache;

        var map = ScanDotNetCommandMethodsCore();
        _dotNetScanFingerprint = fp;
        _dotNetScanCache = map;
        return map;
    }

    /// <summary>可扫描程序集集合 + 磁盘时间戳不变时跳过全量 GetTypes 扫描。</summary>
    internal static string GetDotNetScanFingerprint() => ComputeDotNetScanFingerprint();

    private static string ComputeDotNetScanFingerprint()
    {
        var parts = new List<string>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (!ShouldScanAssembly(asm))
                    continue;

                var label = GetAssemblyLabel(asm);
                var name = asm.FullName;
                if (string.IsNullOrWhiteSpace(name))
                    name = GetAssemblySimpleName(asm);
                if (string.IsNullOrWhiteSpace(name))
                    name = label;

                parts.Add($"{name}|{GetAssemblyWriteTimeUtcTicks(asm)}");
            }
            catch (System.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal($"命令扫描：计算程序集 {GetAssemblyLabel(asm)} 指纹失败，已跳过", ex);
            }
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join("\n", parts);
    }

    private static Dictionary<string, string> ScanDotNetCommandMethodsCore()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var label = GetAssemblyLabel(asm);
            try
            {
                if (!ShouldScanAssembly(asm))
                    continue;

                var types = GetLoadableTypes(asm, label);
                if (types.Count == 0)
                    continue;

                foreach (var type in types)
                {
                    var typeLabel = FormatTypeLabel(type);
                    try
                    {
                        var methods = GetScannableMethods(type, label);
                        if (methods.Count == 0)
                            continue;

                        foreach (var mi in methods)
                        {
                            var name = TryGetCommandMethodGlobalNameSafely(mi, label);
                            if (name == null || name.Length == 0)
                                continue;
                            if (!map.ContainsKey(name))
                                map[name] = label;
                        }
                    }
                    catch (System.Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal($"命令扫描：扫描类型 {typeLabel} 失败（程序集 {label}），已跳过", ex);
                    }
                }
            }
            catch (System.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal($"命令扫描：扫描程序集 {label} 失败，已跳过", ex);
            }
        }

        return map;
    }

    private static string GetAssemblySimpleName(Assembly asm)
    {
        try
        {
            return asm.GetName().Name ?? "?";
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("GetAssemblySimpleName", ex);
            return "?";
        }
    }

    private static long GetAssemblyWriteTimeUtcTicks(Assembly asm)
    {
        try
        {
            var loc = asm.Location;
            if (!string.IsNullOrEmpty(loc) && File.Exists(loc))
                return File.GetLastWriteTimeUtc(loc).Ticks;
        }
        catch (System.Exception ex)
        {
            // 动态/反射加载程序集等可能没有路径
            C_toolsDiagnostics.LogNonFatal("GetAssemblyWriteTimeUtcTicks", ex);
        }

        return 0;
    }

    private static bool ShouldScanAssembly(Assembly asm)
    {
        if (asm.IsDynamic)
            return false;
        var n = GetAssemblySimpleName(asm);
        if (n.StartsWith("System.", StringComparison.Ordinal))
            return false;
        if (n.StartsWith("Microsoft.", StringComparison.Ordinal) &&
            n.IndexOf("AutoCAD", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        if (n.StartsWith("Presentation", StringComparison.Ordinal))
            return false;
        if (string.Equals(n, "AdWindows", StringComparison.OrdinalIgnoreCase))
            return false;
        if (n is "WindowsBase" or "mscorlib" or "netstandard")
            return false;
        if (string.Equals(n, "C_toolsLayerCmdGen", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static string GetAssemblyLabel(Assembly asm)
    {
        try
        {
            var loc = asm.Location;
            if (!string.IsNullOrEmpty(loc))
                return Path.GetFileName(loc);
        }
        catch (System.Exception ex)
        {
            // 某些宿主程序集读取 Location 时会抛异常，退回程序集名
            C_toolsDiagnostics.LogNonFatal("GetAssemblyFileName", ex);
        }

        return GetAssemblySimpleName(asm);
    }

    private static string FormatTypeLabel(Type type)
    {
        var fullName = type.FullName;
        if (!string.IsNullOrWhiteSpace(fullName))
            return fullName;
        return type.Name;
    }

    private static IReadOnlyList<Type> GetLoadableTypes(Assembly asm, string assemblyLabel)
    {
        try
        {
            return asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：程序集 {assemblyLabel} 仅部分类型可加载，继续扫描可用类型", ex);
            return ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }
        catch (FileNotFoundException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取程序集 {assemblyLabel} 类型失败（缺少依赖）", ex);
            return Array.Empty<Type>();
        }
        catch (FileLoadException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取程序集 {assemblyLabel} 类型失败（加载失败）", ex);
            return Array.Empty<Type>();
        }
        catch (TypeLoadException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取程序集 {assemblyLabel} 类型失败（类型加载）", ex);
            return Array.Empty<Type>();
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取程序集 {assemblyLabel} 类型失败（不支持的反射操作）", ex);
            return Array.Empty<Type>();
        }
        catch (NullReferenceException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取程序集 {assemblyLabel} 类型失败（空引用）", ex);
            return Array.Empty<Type>();
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取程序集 {assemblyLabel} 类型失败（未预期异常）", ex);
            return Array.Empty<Type>();
        }
    }

    private static IReadOnlyList<MethodInfo> GetScannableMethods(Type type, string assemblyLabel)
    {
        try
        {
            return type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                   BindingFlags.Instance);
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取类型 {FormatTypeLabel(type)} 的方法失败（程序集 {assemblyLabel}）", ex);
            return Array.Empty<MethodInfo>();
        }
        catch (TypeLoadException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取类型 {FormatTypeLabel(type)} 的方法失败（类型加载，程序集 {assemblyLabel}）", ex);
            return Array.Empty<MethodInfo>();
        }
        catch (FileNotFoundException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取类型 {FormatTypeLabel(type)} 的方法失败（缺少依赖，程序集 {assemblyLabel}）", ex);
            return Array.Empty<MethodInfo>();
        }
        catch (FileLoadException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取类型 {FormatTypeLabel(type)} 的方法失败（加载失败，程序集 {assemblyLabel}）", ex);
            return Array.Empty<MethodInfo>();
        }
        catch (NullReferenceException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取类型 {FormatTypeLabel(type)} 的方法失败（空引用，程序集 {assemblyLabel}）", ex);
            return Array.Empty<MethodInfo>();
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"命令扫描：读取类型 {FormatTypeLabel(type)} 的方法失败（未预期异常，程序集 {assemblyLabel}）", ex);
            return Array.Empty<MethodInfo>();
        }
    }

    private static string? TryGetCommandMethodGlobalNameSafely(MethodInfo method, string assemblyLabel)
    {
        try
        {
            return TryGetCommandMethodGlobalName(method);
        }
        catch (CustomAttributeFormatException ex)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"命令扫描：读取方法 {FormatMethodLabel(method)} 的 CommandMethod 特性失败（格式错误，程序集 {assemblyLabel}）",
                ex);
            return null;
        }
        catch (FileNotFoundException ex)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"命令扫描：读取方法 {FormatMethodLabel(method)} 的 CommandMethod 特性失败（缺少依赖，程序集 {assemblyLabel}）",
                ex);
            return null;
        }
        catch (FileLoadException ex)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"命令扫描：读取方法 {FormatMethodLabel(method)} 的 CommandMethod 特性失败（加载失败，程序集 {assemblyLabel}）",
                ex);
            return null;
        }
        catch (TypeLoadException ex)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"命令扫描：读取方法 {FormatMethodLabel(method)} 的 CommandMethod 特性失败（类型加载，程序集 {assemblyLabel}）",
                ex);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"命令扫描：读取方法 {FormatMethodLabel(method)} 的 CommandMethod 特性失败（无效操作，程序集 {assemblyLabel}）",
                ex);
            return null;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"命令扫描：读取方法 {FormatMethodLabel(method)} 的 CommandMethod 特性失败（参数异常，程序集 {assemblyLabel}）",
                ex);
            return null;
        }
        catch (NullReferenceException ex)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"命令扫描：读取方法 {FormatMethodLabel(method)} 的 CommandMethod 特性失败（空引用，程序集 {assemblyLabel}）",
                ex);
            return null;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"命令扫描：读取方法 {FormatMethodLabel(method)} 的 CommandMethod 特性失败（未预期异常，程序集 {assemblyLabel}）",
                ex);
            return null;
        }
    }

    private static string FormatMethodLabel(MethodInfo method)
    {
        var typeName = method.DeclaringType?.FullName;
        if (!string.IsNullOrWhiteSpace(typeName))
            return typeName + "." + method.Name;
        return method.Name;
    }

    private static string? TryGetCommandMethodGlobalName(MethodInfo method)
    {
        // 同一方法上重复 CommandMethod 时 GetCustomAttribute 会抛异常，只取第一个特性
        try
        {
            var raw = method.GetCustomAttributes(typeof(CommandMethodAttribute), inherit: false);
            if (raw.Length > 0 && raw[0] is CommandMethodAttribute firstAttr)
            {
                var fromProp = TryReadAttributeStringProperty(firstAttr, "GlobalName", "globalName");
                if (!string.IsNullOrWhiteSpace(fromProp))
                    return fromProp!.Trim();
            }
        }
        catch
        {
            // 继续走 CustomAttributeData
        }

        var data = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(CommandMethodAttribute));
        if (data == null)
            return null;
        var args = data.ConstructorArguments;
        if (args.Count >= 2 &&
            args[0].ArgumentType == typeof(string) &&
            args[1].ArgumentType == typeof(string))
            return args[1].Value?.ToString()?.Trim();
        if (args.Count >= 1 && args[0].ArgumentType == typeof(string))
            return args[0].Value?.ToString()?.Trim();
        return null;
    }

    private static string? TryReadAttributeStringProperty(CommandMethodAttribute attr, params string[] names)
    {
        var t = attr.GetType();
        foreach (var name in names)
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p?.GetValue(attr) is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }

        return null;
    }

    /// <summary>
    /// AutoCAD 宿主自带的托管程序集（其 CommandMethod 视为原生命令，非「其他插件」）。
    /// </summary>
    private static bool IsAutodeskHostDll(string fileName)
    {
        var n = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(n))
            return false;
        if (n.StartsWith("Ac", StringComparison.OrdinalIgnoreCase))
            return true;
        if (n.StartsWith("AdWindows", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private static bool BelongsToVCommandTab(string commandName, string? dotNetDllFileName)
    {
        if (FeatureCommandCatalog.ShouldShowOnVCommandTab(commandName, dotNetDllFileName))
            return true;
        if (commandName.StartsWith("V_", StringComparison.OrdinalIgnoreCase))
            return true;
        return commandName.StartsWith("F_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool BelongsToPluginCommandTab(string commandName, string? dotNetDllFileName)
    {
        if (BelongsToVCommandTab(commandName, dotNetDllFileName))
            return false;
        if (FeatureCommandCatalog.ShouldShowOnPluginCommandTab(commandName, dotNetDllFileName))
            return true;
        return dotNetDllFileName is { Length: > 0 } dllFileName && !IsAutodeskHostDll(dllFileName);
    }

    internal static string ResolveCategoryTag(string commandName, string? dotNetDllFileName)
    {
        if (BelongsToVCommandTab(commandName, dotNetDllFileName))
            return TagVCommand;
        if (BelongsToPluginCommandTab(commandName, dotNetDllFileName))
            return TagPluginCommand;
        return TagCadNative;
    }

    /// <summary>
    /// 合并 PGP 与已加载程序集命令：CAD原生命令仅保留「在 PGP 中作为目标出现」的原生命令；
    /// <c>V_*</c>/<c>F_*</c> 单独进入「V命令」页，其余托管/外部插件命令进入「插件命令」页。
    /// </summary>
    internal static List<CommandCatalogRow> MergeCatalog(
        IReadOnlyList<PgpAliasDto> fromPgp,
        IReadOnlyDictionary<string, string> dotNetCommands)
    {
        var pgpTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetToAliases = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in fromPgp)
        {
            if (CadPgpMerge.ShouldIgnorePgpTargetForCommandCatalog(row.Target))
                continue;
            var t = CadPgpMerge.NormalizeTarget(row.Target);
            if (t.Length == 0)
                continue;
            pgpTargets.Add(t);
            if (!targetToAliases.TryGetValue(t, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                targetToAliases[t] = set;
            }

            var a = row.Alias ?? "";
            if (a.Length > 0)
                set.Add(a);
        }

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in pgpTargets)
            all.Add(t);
        foreach (var t in AcadNativeCommandDescriptions.CommandNames)
            all.Add(t);
        foreach (var k in dotNetCommands.Keys)
            all.Add(k);
        all.Add(PluginCommandIds.Launcher);
        all.Add(PluginCommandIds.Layer);

        var rows = new List<CommandCatalogRow>();
        foreach (var cmd in all.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (FeatureCommandCatalog.ShouldHideFromCatalog(cmd))
                continue;

            dotNetCommands.TryGetValue(cmd, out var dll);
            string? dotNetDll = dll;
            var resolved = ResolveCategoryTag(cmd, dotNetDll);

            if (resolved is TagVCommand or TagPluginCommand)
            {
                if (string.IsNullOrEmpty(dotNetDll) && !FeatureCommandCatalog.IsOwnedByCtools(cmd))
                    continue;
            }
            else
            {
                // CAD原生命令：来自 PGP 目标或 C_TOOL 内置原生命令表。
                if (!pgpTargets.Contains(cmd) && !AcadNativeCommandDescriptions.Contains(cmd))
                    continue;
            }

            var rowTag = resolved;

            var aliasStr = targetToAliases.TryGetValue(cmd, out var al) && al.Count > 0
                ? string.Join(", ", al.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                : "—";

            var srcParts = new List<string>();
            if (!aliasStr.Equals("—", StringComparison.Ordinal))
                srcParts.Add("PGP");
            if (!string.IsNullOrEmpty(dotNetDll))
                srcParts.Add(dotNetDll);
            if (srcParts.Count == 0 && rowTag == TagCadNative && AcadNativeCommandDescriptions.Contains(cmd))
                srcParts.Add("CAD内置");

            var source = srcParts.Count > 0 ? string.Join(" · ", srcParts) : "—";
            var catalogRow = new CommandCatalogRow(cmd, aliasStr, source, rowTag);
            if (CommandDescriptionDefaults.TryGet(cmd, rowTag, out var builtInDescription))
                catalogRow.Description = builtInDescription;

            rows.Add(catalogRow);
        }

        return rows;
    }
}
