using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace C_toolsAaaPlugin;

public class AaaCommands
{
    private static readonly ModelessWindowHost<AaaPanelWindow> s_panelHost = new();

    [CommandMethod(AaaPluginCommandIds.CommandGroup, AaaPluginCommandIds.Aaa, CommandFlags.Modal)]
    [CommandMethod(AaaPluginCommandIds.CommandGroup, C_toolsCommandIds.Aaa.AliasShort, CommandFlags.Modal)]
    public void ToggleAaaPanel()
    {
        if (!CadCommandContext.TryGetActiveDocument("打开/切换 V_AAA 面板", out _, out var editor) ||
            editor == null)
        {
            return;
        }

        try
        {
            s_panelHost.Toggle(() => new AaaPanelWindow());
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开/切换 V_AAA 面板失败", ex);
            CadCommandContext.TryWriteMessage(
                editor,
                $"\nV_AAA：打开面板失败：{ex.Message}",
                "写入 V_AAA 面板错误消息失败");
        }
    }

    [CommandMethod(AaaPluginCommandIds.CommandGroup, AaaPluginCommandIds.Ql, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void RunQlBlockList()
    {
        if (!CadCommandContext.TryGetActiveDocument("执行 F_QL 命令", out _, out var editor) ||
            editor == null)
        {
            return;
        }

        try
        {
            var assembly = GetOrLoadQlPluginAssembly();
            var commandType = assembly.GetType("QlPlugin.QlCommand", throwOnError: true);
            var method = commandType?.GetMethod("ShowBlockList", BindingFlags.Instance | BindingFlags.Public);
            if (method == null)
                throw new MissingMethodException("QlPlugin.QlCommand", "ShowBlockList");

            var instance = Activator.CreateInstance(commandType!);
            if (instance == null)
                throw new InvalidOperationException("无法创建 QlPlugin.QlCommand 实例。");

            method.Invoke(instance, null);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_QL 执行 QL 失败（无效操作）", ex);
            CadCommandContext.TryWriteMessage(
                editor,
                $"\nF_QL：执行 QL 失败：{ex.Message}",
                "写入 V_AAA 面板错误消息失败");
        }
        catch (TargetInvocationException ex)
        {
            var actual = ex.InnerException ?? ex;
            C_toolsDiagnostics.LogNonFatal("F_QL 执行 QL 失败（反射调用）", actual);
            CadCommandContext.TryWriteMessage(
                editor,
                $"\nF_QL：执行 QL 失败：{actual.Message}",
                "写入 V_AAA 面板错误消息失败");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_QL 执行 QL 失败", ex);
            CadCommandContext.TryWriteMessage(
                editor,
                $"\nF_QL：执行 QL 失败：{ex.Message}",
                "写入 V_AAA 面板错误消息失败");
        }
    }

    internal static void CloseAaaPanelIfAny()
    {
        s_panelHost.CloseIfAny(
            onInvalidOperation: ex => C_toolsDiagnostics.LogNonFatal("关闭 V_AAA 面板失败（无效操作）", ex),
            onError: ex => C_toolsDiagnostics.LogNonFatal("关闭 V_AAA 面板失败", ex));
    }

    private static Assembly GetOrLoadQlPluginAssembly()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(static x => string.Equals(x.GetName().Name, "QlPlugin", StringComparison.OrdinalIgnoreCase));
        if (loaded != null)
            return loaded;

        var candidatePaths = GetQlPluginCandidatePaths();
        var loadErrors = new List<string>();
        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                return Assembly.LoadFrom(path);
            }
            catch (System.Exception ex) when (ex is FileLoadException || ex is FileNotFoundException || ex is BadImageFormatException)
            {
                loadErrors.Add($"{path} -> {ex.Message}");
            }
        }

        if (loadErrors.Count > 0)
        {
            throw new FileLoadException(
                BuildQlPluginLoadFailureMessage(candidatePaths, loadErrors));
        }

        throw new FileNotFoundException(
            BuildQlPluginMissingMessage(candidatePaths),
            string.Join(" | ", candidatePaths));
    }

    private static string[] GetQlPluginCandidatePaths()
    {
        var baseDirectories = new[]
        {
            Path.GetDirectoryName(typeof(AaaCommands).Assembly.Location),
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in baseDirectories)
        {
            AddQlPluginSearchDirectory(directories, directory);
        }

        foreach (var directory in baseDirectories)
        {
            foreach (var nearbyDirectory in EnumerateNearbyBundleDirectories(directory))
            {
                AddQlPluginSearchDirectory(directories, nearbyDirectory);
            }
        }

        return directories
            .Select(static x => Path.Combine(x, "QlPlugin.dll"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddQlPluginSearchDirectory(ISet<string> directories, string? directory)
    {
        var normalized = NormalizeDirectory(directory);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        directories.Add(normalized!);
    }

    private static IEnumerable<string> EnumerateNearbyBundleDirectories(string? startDirectory)
    {
        var currentDirectory = NormalizeDirectory(startDirectory);
        for (var depth = 0; !string.IsNullOrWhiteSpace(currentDirectory) && depth < 6; depth++)
        {
            var contentsDirectory = Path.Combine(currentDirectory!, "Contents", "Win64");
            if (Directory.Exists(contentsDirectory))
                yield return contentsDirectory;

            var bundle2024Directory = Path.Combine(currentDirectory!, "C_TOOL_2024.bundle", "Contents", "Win64");
            if (Directory.Exists(bundle2024Directory))
                yield return bundle2024Directory;

            currentDirectory = NormalizeDirectory(Directory.GetParent(currentDirectory!)?.FullName);
        }
    }

    private static string? NormalizeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        try
        {
            return Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildQlPluginLoadFailureMessage(IEnumerable<string> candidatePaths, IEnumerable<string> loadErrors)
    {
        var builder = new StringBuilder();
        builder.AppendLine("已找到 QlPlugin.dll，但没有可供 AutoCAD 2024 加载的 .NET Framework 4.8 兼容版本。");
        builder.AppendLine("已尝试的位置：");
        foreach (var path in candidatePaths)
        {
            builder.AppendLine("- " + path);
        }

        builder.AppendLine("加载错误：");
        foreach (var error in loadErrors)
        {
            builder.AppendLine("- " + error);
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildQlPluginMissingMessage(IEnumerable<string> candidatePaths)
    {
        var builder = new StringBuilder();
        builder.AppendLine("未找到 QlPlugin.dll。请先将与当前 AutoCAD 版本匹配的 DLL 放到当前 C_TOOL 插件目录（建议 Contents\\Win64），或放到桌面。");
        builder.AppendLine("已尝试的候选位置（以下路径不代表文件一定存在）：");
        foreach (var path in candidatePaths)
        {
            builder.AppendLine("- " + path);
        }

        return builder.ToString().TrimEnd();
    }
}
