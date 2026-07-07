using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;

namespace C_toolsPlugin;

/// <summary>
/// 将图层别名写成 AutoLISP <c>(defun c:A1 …)</c>，保存后 <c>(load)</c> 即可重定义命令，无需重启 CAD。
/// 所有快捷键统一调用 <c>F_Layer</c>，填充处理在 .NET 端完成。
/// </summary>
internal static class LayerLispShortcuts
{
    internal const string FileName = "c_tools_layer_shortcuts.lsp";
    private const string GeneratedAliasSymbolsVar = "*ctools_layer_alias_symbols*";

    /// <summary>旧版（cadplus_*）文件名；保存新文件后尝试删除，加载时仍可作为回退路径。</summary>
    private const string LegacyOldLayerLispFileName = "cadplus_layer_shortcuts.lsp";

    internal static string GetFullPath()
    {
        C_toolsPaths.EnsureFolders();
        return Path.Combine(C_toolsPaths.LayerShortcutsDataFolder, FileName);
    }

    internal static string LegacySupportLispPath =>
        Path.Combine(C_toolsPaths.SupportFolder, FileName);

    private static string LegacyOldLispUserPath =>
        Path.Combine(C_toolsPaths.UserConfigFolder, LegacyOldLayerLispFileName);

    private static string LegacyOldLispSupportPath =>
        Path.Combine(C_toolsPaths.SupportFolder, LegacyOldLayerLispFileName);

    internal static (int Count, List<string> Skipped) EmitFromEntries(IReadOnlyList<LayerShortcutEntry> layers)
    {
        C_toolsPaths.EnsureFolders();
        var script = BuildScript(layers, out var count, out var skipped);

        var path = GetFullPath();
        File.WriteAllText(path, script, new UTF8Encoding(false));
        TryDeleteLegacySupportLisp();
        TryDeleteLegacyLayerGenDll();
        return (count, skipped);
    }

    internal static string BuildScript(IReadOnlyList<LayerShortcutEntry> layers, out int count, out List<string> skipped)
    {
        skipped = new List<string>();
        var generatedAliases = new List<string>();

        var sb = new StringBuilder();
        sb.AppendLine("; C_TOOL — auto-generated layer shortcuts");
        sb.AppendLine("; 所有快捷键统一调用 F_Layer");
        sb.AppendLine("(vl-load-com)");
        sb.AppendLine();
        sb.AppendLine("; 先撤销上一版已生成命令，确保删除别名后立刻失效");
        sb.AppendLine("; Protected CAD command names are never undefuned.");
        sb.AppendLine($"(setq _ctools_layer_protected_cmds {BuildProtectedAliasSymbolList()})");
        sb.AppendLine($"(foreach _ctools_cmd (if (boundp '{GeneratedAliasSymbolsVar}) {GeneratedAliasSymbolsVar} '())");
        sb.AppendLine("  (if (not (member _ctools_cmd _ctools_layer_protected_cmds))");
        sb.AppendLine("    (vl-catch-all-apply 'vl-acad-undefun (list _ctools_cmd))");
        sb.AppendLine("  )");
        sb.AppendLine(")");
        sb.AppendLine("(foreach _ctools_retired '(c:KKK c:V_KKK)");
        sb.AppendLine("  (vl-catch-all-apply 'vl-acad-undefun (list _ctools_retired))");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("; LAYFRZ fallback: CAD 自带图层冻结命令缺失时，转到 C_TOOL 兼容实现");
        sb.AppendLine("(defun c:LAYFRZ ()");
        sb.AppendLine($"  (command \"._{PluginCommandIds.LayerFreezeFallback}\")");
        sb.AppendLine("  (princ)");
        sb.AppendLine(")");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        count = 0;
        foreach (var e in layers.OrderBy(x => CadPgpMerge.NormalizeAlias(x.Alias), StringComparer.OrdinalIgnoreCase))
        {
            var a = CadPgpMerge.NormalizeAlias(e.Alias);
            if (a.Length == 0 || string.IsNullOrWhiteSpace(e.LayerName))
                continue;
            if (!seen.Add(a))
                continue;
            if (!LayerAliasRules.IsValidGeneratedCommandAlias(a, out var why))
            {
                skipped.Add($"{a}：{why}");
                continue;
            }

            var lit = EscapeLispString(a);
            sb.AppendLine($"(defun c:{a} ()");
            sb.AppendLine($"  (setq _ctools_sv_u1 (getvar \"USERS1\"))");
            sb.AppendLine($"  (setvar \"USERS1\" \"{lit}\")");
            sb.AppendLine($"  (command \"._F_Layer\")");
            sb.AppendLine($"  (setvar \"USERS1\" _ctools_sv_u1)");
            sb.AppendLine($"  (princ)");
            sb.AppendLine(")");
            generatedAliases.Add(a);
            count++;
        }

        if (count == 0)
            sb.AppendLine("(princ)");

        sb.AppendLine();
        sb.AppendLine("; 记录当前已生成命令，供下次热重载前先撤销旧定义");
        sb.AppendLine($"(setq {GeneratedAliasSymbolsVar} {BuildGeneratedAliasSymbolList(generatedAliases)})");
        sb.AppendLine("(princ)");
        return sb.ToString();
    }

    internal static string EscapeLispString(string a) =>
        a.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string BuildGeneratedAliasSymbolList(IReadOnlyList<string> aliases)
    {
        if (aliases.Count == 0)
            return "'()";
        return $"'({string.Join(" ", aliases.Select(static a => $"c:{a}"))})";
    }

    private static string BuildProtectedAliasSymbolList()
    {
        var aliases = LayerAliasRules.ProtectedCadCommandNames;
        if (aliases.Count == 0)
            return "'()";
        return $"'({string.Join(" ", aliases.Select(static a => $"c:{a}"))})";
    }

    internal static void TryLoadInDocument(Document doc)
    {
        var path = ResolveLispPathForLoad();
        if (path == null)
            return;
        var p = path.Replace("\\", "/");
        doc.SendStringToExecute(BuildLoadCommand(p), true, false, false);
    }

    internal static string BuildLoadCommand(string normalizedPath) =>
        $"(progn (load \"{normalizedPath}\") (princ))\n";

    private static string? ResolveLispPathForLoad()
    {
        var cur = GetFullPath();
        if (File.Exists(cur))
            return cur;
        var supportNew = LegacySupportLispPath;
        if (File.Exists(supportNew))
            return supportNew;
        if (File.Exists(LegacyOldLispUserPath))
            return LegacyOldLispUserPath;
        if (File.Exists(LegacyOldLispSupportPath))
            return LegacyOldLispSupportPath;
        return null;
    }

    private static void TryDeleteLegacySupportLisp()
    {
        try
        {
            var cur = GetFullPath();
            foreach (var p in new[] { LegacySupportLispPath, LegacyOldLispUserPath, LegacyOldLispSupportPath })
            {
                if (string.Equals(p, cur, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (File.Exists(p))
                    File.Delete(p);
            }
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("删除旧版 LISP 文件失败（IO错误）", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("删除旧版 LISP 文件失败（权限不足）", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("删除旧版 LISP 文件失败", ex);
        }
    }

    private static void TryDeleteLegacyLayerGenDll()
    {
        try
        {
            var loc = typeof(PluginApp).Assembly.Location;
            if (string.IsNullOrEmpty(loc))
                return;
            var dir = Path.GetDirectoryName(loc);
            if (string.IsNullOrEmpty(dir))
                return;
            var legacy = Path.Combine(dir, "C_toolsLayerCmdGen.dll");
            if (File.Exists(legacy))
                File.Delete(legacy);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("删除旧版 DLL 失败（IO错误）", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("删除旧版 DLL 失败（权限不足）", ex);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("删除旧版 DLL 失败", ex);
        }
    }
}
