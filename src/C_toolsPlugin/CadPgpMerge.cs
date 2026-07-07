using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
namespace C_toolsPlugin;

internal static class CadPgpMerge
{
    internal const string BeginMarker = "; --- C_TOOL aliases begin ---";
    internal const string EndMarker = "; --- C_TOOL aliases end ---";
    private const string BackupDirectoryName = "C_TOOL_Backups";
    private const string BackupFileMarker = ".c_tools_bak_";
    private const int MaxBackupFilesToKeep = 5;
    private const string RetiredLauncherTarget = "V_KKK";

    /// <summary>历史旧启动器块（含非法 *^C^C_.CADGO 宏，易触发 PGP 第 3 字段错误）。</summary>
    private const string LegacyLauncherBlockBegin = ";; == C-TOOL KKK 快捷 BEGIN";
    private const string LegacyLauncherBlockEnd = ";; == C-TOOL KKK 快捷 END";

    private static readonly Regex InvalidAliasChars = new(@"[\s,;*]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] RetiredLauncherAliases = ["KKK", "V_KKK"];
    private static readonly string[] NewlineSeparators = ["\r\n", "\r", "\n"];

    /// <summary>与英文逗号类似、写入 PGP 后易导致解析异常的符号（目标中一律禁止）。</summary>
    private static readonly char[] CommaLikeChars =
    [
        '\uFF0C', // ，
        '\uFF64', '\u3001', '\u060C', '\uFE50', '\uFE51', '\uFF0E', // ． 等易混淆句读
    ];

    /// <summary>
    /// 生成 PGP 片段；同一别名多行时后者覆盖前者。别名会去掉空白与非法字符。
    /// 定义段若含逗号或换行，会被 acad.pgp 拆成第三字段并报错（如「第 3 字段 / 缺少内存大小定义」），此类行会被跳过并记入 <paramref name="skipped"/>。
    /// </summary>
    internal static string BuildAliasBlock(IReadOnlyList<PgpAliasDto> rows, List<string>? skipped = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var rawAlias = NormalizeAlias(r.Alias);
            var a = SanitizePgpAliasKey(rawAlias, out var aliasWhy);
            if (aliasWhy.Length > 0)
            {
                skipped?.Add($"{rawAlias}：{aliasWhy}");
                continue;
            }

            if (LayerAliasRules.IsProtectedCadCommandName(a))
            {
                skipped?.Add($"{a}：与 CAD 自带命令同名");
                continue;
            }

            var t = NormalizeTarget(r.Target);
            if (a.Length == 0 || t.Length == 0)
                continue;

            if (IsLegacyLayerShortcutPgpMacro(t))
            {
                skipped?.Add($"{a}：图层快捷由 LISP + {PluginCommandIds.Layer} 处理，不写 acad.pgp（已忽略旧版图层错误宏）");
                continue;
            }

            if (!IsSafePgpDefinition(t, out var why))
            {
                skipped?.Add($"{a}：{why}");
                continue;
            }

            map[a] = t;
        }

        var sb = new StringBuilder();
        sb.AppendLine(BeginMarker);
        foreach (var kv in map.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            // 仅用「第一个逗号」分隔别名与定义；定义内禁止逗号。宏与 * 命令均写作「别名,定义」无空格，减少部分环境解析差异。
            if (kv.Value.StartsWith("^", StringComparison.Ordinal))
                sb.AppendLine($"{kv.Key},{kv.Value}");
            else
                sb.AppendLine($"{kv.Key},*{kv.Value}");
        }

        sb.AppendLine(EndMarker);
        return sb.ToString();
    }

    internal static bool ContainsRetiredLauncherAliasEntries(string text)
    {
        return ParsePgpAliasLinesFromText(text)
            .Any(x => IsRetiredLauncherAliasEntry(x.Alias, x.Target));
    }

    internal static string BuildSanitizedManagedAliasBlock(string text)
    {
        var filtered = ParsePgpAliasLinesFromText(text)
            .Where(x => !IsRetiredLauncherAliasEntry(x.Alias, x.Target))
            .Where(x => !IsLegacyLayerShortcutPgpMacro(NormalizeTarget(x.Target)))
            .ToList();
        return BuildAliasBlock(filtered);
    }

    /// <summary>
    /// PGP 别名键：仅 ASCII 字母、数字、下划线、连字符（与 AutoCAD 常见别名一致，避免不可见或全角字符导致异常行）。
    /// </summary>
    private static string SanitizePgpAliasKey(string normalizedAlias, out string skipReason)
    {
        skipReason = "";
        if (normalizedAlias.Length == 0)
            return "";
        var sb = new StringBuilder(normalizedAlias.Length);
        foreach (var c in normalizedAlias)
        {
            if (CharAsciiCompat.IsAsciiLetterOrDigit(c) || c is '_' or '-')
                sb.Append(c);
        }

        var s = sb.ToString();
        if (s.Length == 0)
        {
            skipReason = "别名须为英文字母、数字、下划线或连字符（已去除中文等非法字符）";
            return "";
        }

        if (s.Length > 64)
        {
            skipReason = "别名过长（>64）";
            return "";
        }

        if (s != normalizedAlias)
            skipReason = "别名含非 ASCII 字母数字/下划线/连字符，已跳过（请改用英文别名）";

        return skipReason.Length > 0 ? "" : s;
    }

    private static bool IsSafePgpDefinition(string definitionNormalized, out string reason)
    {
        reason = "";
        if (definitionNormalized.IndexOfAny(['\r', '\n']) >= 0)
        {
            reason = "目标/宏中不能含换行";
            return false;
        }

        if (definitionNormalized.IndexOf(',') >= 0)
        {
            reason = "目标/宏中不能含英文逗号，否则 PGP 会解析出多余字段（报「第 3 字段 / 缺少内存大小定义」）";
            return false;
        }

        if (definitionNormalized.IndexOfAny(CommaLikeChars) >= 0)
        {
            reason = "目标/宏中不能含全角逗号等易混淆符号";
            return false;
        }

        foreach (var c in definitionNormalized)
        {
            if (c < 32 && c != '\t')
            {
                reason = "目标/宏中不能含控制字符";
                return false;
            }
        }

        var isMacro = definitionNormalized.StartsWith("^", StringComparison.Ordinal);
        if (!isMacro && definitionNormalized.IndexOf(';') >= 0)
        {
            reason = "命令名中不能含分号（分号在 PGP 中易与注释混淆）";
            return false;
        }

        if (!isMacro)
        {
            foreach (var c in definitionNormalized)
            {
                if (CharAsciiCompat.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
                    continue;
                reason = "命令名仅允许英文字母、数字、下划线、点、连字符（不要空格、括号或中文）";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 与合并校验一致：若该行会触发 PGP「第 3 字段」等解析错误则返回原因，否则 null（含空行、注释、非「别名,…」行）。
    /// </summary>
    private static string? ClassifyInvalidPgpAliasLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
            return null;

        var macroLike = line.IndexOf('^') >= 0 && line.IndexOf('^') > line.IndexOf(',');
        if (!macroLike)
        {
            var semi = line.IndexOf(';');
            if (semi >= 0)
                line = line[..semi].Trim();
        }

        var comma = line.IndexOf(',');
        if (comma <= 0)
            return null;

        var def = line[(comma + 1)..].Trim();
        if (def.Length == 0)
            return "逗号后缺少命令定义";

        if (!macroLike)
        {
            var cmt = def.IndexOf(" ;", StringComparison.Ordinal);
            if (cmt >= 0)
                def = def[..cmt].Trim();
        }

        if (def.IndexOf(',') >= 0)
            return "定义中含英文逗号，会触发「第 3 字段 / 缺少内存大小定义」。请删改逗号或改为宏外注释";

        return null;
    }

    /// <summary>
    /// 合并后的全文检查：非注释行在「别名,定义」中，定义段不得再含英文逗号（否则 REINIT 报第 3 字段）。
    /// </summary>
    internal static (bool Ok, string Message) ValidateMergedPgpContent(string fullText)
    {
        var lineNum = 0;
        var lines = fullText.Split(NewlineSeparators, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            lineNum++;
            var reason = ClassifyInvalidPgpAliasLine(lines[i]);
            if (reason != null)
            {
                return (false,
                    $"acad.pgp 第 {lineNum} 行：{reason}。\n" +
                    $"内容预览：{TruncateForMessage(lines[i].Trim(), 120)}");
            }
        }

        return (true, "");
    }

    private static string TruncateForMessage(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";

    /// <summary>
    /// 将 C_TOOL 别名块写入 <see cref="C_toolsPaths.UserAcadPgp"/>（仅与<strong>本目录已有</strong> acad.pgp 合并，<strong>不读取</strong> AutoCAD 安装目录下的模板）；
    /// 另在 <see cref="C_toolsPaths.UserSiblingC_toolsAliasesPgpPath"/> 写入<strong>仅含</strong> C_TOOL 块的副本（…\User\C_TOOL_command_aliases.pgp）。
    /// 首次保存若尚无目标 acad.pgp，则只写入 C_TOOL 块（建议从安装介质复制一份完整 acad.pgp 到该路径所在目录后再保存以保留系统别名）。
    /// </summary>
    internal static (bool Ok, string Message) ApplyToDiscoveredPgp(Document? doc, string newBlock)
    {
        _ = doc;
        try
        {
            CadPgpSupportPath.EnsureC_toolsSupportFirst();
            C_toolsPaths.EnsureFolders();

            string mergeSourcePath;
            string original;
            if (File.Exists(C_toolsPaths.UserAcadPgpPath))
            {
                mergeSourcePath = C_toolsPaths.UserAcadPgpPath;
                original = File.ReadAllText(mergeSourcePath, DetectEncoding(mergeSourcePath));
            }
            else
            {
                mergeSourcePath = "（新建，未合并 AutoCAD 安装目录 acad.pgp）";
                original = "";
            }

            var stripped = SanitizePgpBaseBeforeC_toolsMerge(original).TrimEnd();
            var merged = stripped.Length == 0
                ? newBlock.TrimEnd() + Environment.NewLine
                : stripped + Environment.NewLine + Environment.NewLine + newBlock.TrimEnd() + Environment.NewLine;

            var check = ValidateMergedPgpContent(merged);
            if (!check.Ok)
            {
                return (false,
                    check.Message + Environment.NewLine + Environment.NewLine +
                    $"当前合并将写入：{C_toolsPaths.UserAcadPgpPath}" + Environment.NewLine +
                    "未写入。请先修正上述行（多为旧版图层宏含多余逗号或非法格式）。");
            }

            var outPath = C_toolsPaths.UserAcadPgpPath;
            var enc = File.Exists(outPath)
                ? DetectEncoding(outPath)
                : new UTF8Encoding(true);
            var writeResult = WriteMergedPgpWithManagedBackup(outPath, merged, enc, originalContent: original);

            var standalone = newBlock.TrimEnd() + Environment.NewLine;
            File.WriteAllText(C_toolsPaths.UserSiblingC_toolsAliasesPgpPath, standalone, new UTF8Encoding(true));

            var writeSummary = writeResult.FileChanged
                ? $"已写入（优先加载）：{outPath}"
                : $"未改动：{outPath}（内容相同，未重复写入）";
            if (!string.IsNullOrWhiteSpace(writeResult.BackupPath))
                writeSummary += Environment.NewLine + $"备份：{writeResult.BackupPath}";

            return (true,
                writeSummary + Environment.NewLine +
                $"合并基底：{mergeSourcePath}{Environment.NewLine}" +
                $"C_TOOL 块副本（仅别名块）：{C_toolsPaths.UserSiblingC_toolsAliasesPgpPath}{Environment.NewLine}" +
                $"已将「{Path.GetDirectoryName(C_toolsPaths.UserAcadPgpPath) ?? C_toolsPaths.SupportFolder}」置于支持文件搜索路径首位；请 REINIT 勾选 PGP 或重启 CAD。");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("CadPgpMerge 写入 PGP 失败", ex);
            return (false, $"写入失败：{ex.Message}");
        }
    }

    private static string RemoveMarkedBlock(string content, string beginMarker, string endMarker)
    {
        var s = content;
        while (true)
        {
            var i = s.IndexOf(beginMarker, StringComparison.Ordinal);
            if (i < 0)
                break;
            var j = s.IndexOf(endMarker, i, StringComparison.Ordinal);
            if (j < 0)
            {
                s = s.Remove(i, beginMarker.Length);
                continue;
            }

            j += endMarker.Length;
            while (j < s.Length && (s[j] == '\r' || s[j] == '\n'))
                j++;
            s = s.Remove(i, j - i);
        }

        return s;
    }

    internal static string GetBackupDirectoryPath(string pgpPath)
    {
        var baseDirectory = Path.GetDirectoryName(pgpPath);
        return string.IsNullOrWhiteSpace(baseDirectory)
            ? BackupDirectoryName
            : Path.Combine(baseDirectory, BackupDirectoryName);
    }

    internal static string BuildBackupFilePath(string pgpPath, DateTime timestamp, string uniqueSuffix)
    {
        var fileName = Path.GetFileName(pgpPath) + BackupFileMarker + timestamp.ToString("yyyyMMdd_HHmmss") + "_" + uniqueSuffix;
        return Path.Combine(GetBackupDirectoryPath(pgpPath), fileName);
    }

    internal static string[] GetManagedBackupFiles(string pgpPath)
    {
        var backupDirectory = GetBackupDirectoryPath(pgpPath);
        if (!Directory.Exists(backupDirectory))
            return [];

        var fileNamePrefix = Path.GetFileName(pgpPath) + BackupFileMarker;
        return Directory.GetFiles(backupDirectory, fileNamePrefix + "*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void PruneManagedBackupFiles(string pgpPath, int maxFilesToKeep = MaxBackupFilesToKeep)
    {
        if (maxFilesToKeep < 0)
            maxFilesToKeep = 0;

        var backups = GetManagedBackupFiles(pgpPath);
        for (var i = maxFilesToKeep; i < backups.Length; i++)
            File.Delete(backups[i]);
    }

    internal static (bool FileChanged, string? BackupPath) WriteMergedPgpWithManagedBackup(string outPath, string mergedContent, Encoding encoding, string? originalContent = null)
    {
        var exists = File.Exists(outPath);
        if (exists)
        {
            var original = originalContent ?? File.ReadAllText(outPath, DetectEncoding(outPath));
            if (string.Equals(original, mergedContent, StringComparison.Ordinal))
                return (false, null);

            var backupDirectory = GetBackupDirectoryPath(outPath);
            Directory.CreateDirectory(backupDirectory);
            var backupPath = BuildBackupFilePath(outPath, DateTime.Now, Guid.NewGuid().ToString("N")[..8]);
            File.Copy(outPath, backupPath, overwrite: false);
            File.WriteAllText(outPath, mergedContent, encoding);
            PruneManagedBackupFiles(outPath);
            return (true, backupPath);
        }

        File.WriteAllText(outPath, mergedContent, encoding);
        return (true, null);
    }

    internal static string RemoveAllC_toolsBlocks(string content) =>
        RemoveMarkedBlock(content, BeginMarker, EndMarker);

    /// <summary>去掉 C-TOOL 段、旧 C_TOOL 段、以及散落的旧版图层错误宏 PGP 行（图层统一使用 <see cref="PluginCommandIds.Layer"/>）。</summary>
    internal static string SanitizePgpBaseBeforeC_toolsMerge(string content)
    {
        var s = RemoveMarkedBlock(content, LegacyLauncherBlockBegin, LegacyLauncherBlockEnd);
        s = RemoveAllC_toolsBlocks(s);
        s = RemoveLegacyLayerProxyPgpLines(s);
        s = RemoveRetiredLauncherAliasPgpLines(s);
        s = RemoveInvalidCommaFieldPgpLines(s);
        return s;
    }

    /// <summary>
    /// 删除会触发「第 3 字段」的别名行（定义段含多余英文逗号等），改为分号注释保留痕迹；否则合并保存会整单失败。
    /// </summary>
    private static string RemoveInvalidCommaFieldPgpLines(string content)
    {
        using var sr = new StringReader(content);
        var sb = new StringBuilder();
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (ClassifyInvalidPgpAliasLine(line) == null)
            {
                sb.AppendLine(line);
                continue;
            }

            var preview = line.Trim();
            if (preview.Length > 200)
                preview = preview[..200] + "…";
            sb.AppendLine($"; C_TOOL 已移除非法 PGP 行（易触发第 3 字段 / 解析错误）: {preview}");
        }

        return sb.ToString();
    }

    /// <summary>删除含 <c>CADGO</c> 或历史错误图层命令宏、易触发「第 3 字段」的旧 PGP 行。</summary>
    private static string RemoveLegacyLayerProxyPgpLines(string content)
    {
        using var sr = new StringReader(content);
        var sb = new StringBuilder();
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            if (IsLegacyLayerProxyPgpLine(line.Trim()))
                continue;
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    /// <summary>删除已退役的历史面板入口别名，避免旧版块或手工残留继续指向已移除入口。</summary>
    private static string RemoveRetiredLauncherAliasPgpLines(string content)
    {
        using var sr = new StringReader(content);
        var sb = new StringBuilder();
        string? line;
        while ((line = sr.ReadLine()) != null)
        {
            var parsed = ParsePgpAliasLinesFromText(line);
            if (parsed.Count == 1 && IsRetiredLauncherAliasEntry(parsed[0].Alias, parsed[0].Target))
                continue;
            sb.AppendLine(line);
        }

        return sb.ToString();
    }

    private static bool IsLegacyLayerProxyPgpLine(string trimmed)
    {
        if (trimmed.Length == 0 || trimmed.StartsWith(";", StringComparison.Ordinal))
            return false;
        var comma = trimmed.IndexOf(',');
        if (comma <= 0)
            return false;
        var def = trimmed[(comma + 1)..].Trim();
        return IsScrapLayerHotkeyMacroDefinition(def);
    }

    /// <summary>
    /// 逗号后的 PGP 定义：若为旧版图层一键宏（含 ^C^C、-LAYER;S;、CHPROP 切层、CADGO、VLYRGO 等），保存合并时应整行删除。
    /// </summary>
    private static bool IsScrapLayerHotkeyMacroDefinition(string def)
    {
        if (def.Length == 0 || def.IndexOf('^') < 0)
            return false;
        var c = def.Replace(" ", "").Replace("\t", "");
        if (StringSearchCompat.ContainsOrdinalIgnoreCase(c, "_.CADGO") ||
            StringSearchCompat.ContainsOrdinalIgnoreCase(c, ".CADGO"))
            return true;
        if (MatchesDeprecatedLayerMacroToken(def))
            return true;
        if (StringSearchCompat.ContainsOrdinalIgnoreCase(c, "_-LAYER;S;") ||
            StringSearchCompat.ContainsOrdinalIgnoreCase(c, "_.-LAYER;S;"))
            return true;
        if (StringSearchCompat.ContainsOrdinalIgnoreCase(c, "_CHPROP;;_LA;") ||
            StringSearchCompat.ContainsOrdinalIgnoreCase(c, "_.CHPROP;;_LA;"))
            return true;
        return false;
    }

    internal static string NormalizeAlias(string? raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0)
            return "";
        t = InvalidAliasChars.Replace(t, "");
        return t;
    }

    /// <summary>与表格「别名」单元格一致：英文/中文逗号、分号拆成多个代号（勿对整格调用 <see cref="NormalizeAlias"/>，否则会去掉分隔符把多代号粘成一串）。</summary>
    internal static readonly char[] AliasCellSeparators = [',', ';', '\uFF0C', '\uFF1B'];

    internal static IEnumerable<string> EnumerateNormalizedAliasTokensFromCell(string? cell)
    {
        var t = (cell ?? "").Trim();
        if (t.Length == 0)
            yield break;
        foreach (var part in t.Split(AliasCellSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            var a = NormalizeAlias(part);
            if (a.Length > 0)
                yield return a;
        }
    }

    internal static string NormalizeTarget(string? raw)
    {
        var t = (raw ?? "").Trim();
        if (t.Length == 0)
            return "";
        if (t.StartsWith("*", StringComparison.Ordinal))
            t = t.TrimStart('*').Trim();
        return t;
    }

    private static bool IsRetiredLauncherAliasEntry(string? alias, string? target)
    {
        var a = NormalizeAlias(alias);
        var t = NormalizeTarget(target);
        if (string.Equals(t, RetiredLauncherTarget, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var retiredAlias in RetiredLauncherAliases)
        {
            if (string.Equals(a, retiredAlias, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 历史 PGP 曾误用错误图层命令宏；现图层仅 <see cref="PluginCommandIds.Layer"/> + c_tools_layer_shortcuts.lsp 别名，此类宏不得进入 C_TOOL 块。
    /// </summary>
    internal static bool IsLegacyLayerShortcutPgpMacro(string normalizedTarget)
    {
        var t = normalizedTarget;
        if (t.Length == 0)
            return false;
        return IsScrapLayerHotkeyMacroDefinition(t);
    }

    /// <summary>
    /// 识别历史错误图层命令拼写（去下划线/空白后含连续 VLYRGO）；<see cref="PluginCommandIds.Layer"/> 规范化为 VLayer，不会命中。
    /// </summary>
    internal static bool MatchesDeprecatedLayerMacroToken(string s)
    {
        if (s.Length == 0)
            return false;
        var c = s.Replace("_", "").Replace(" ", "").Replace("\t", "");
        return StringSearchCompat.ContainsOrdinalIgnoreCase(c, "VLYRGO");
    }

    /// <summary>
    /// 合并命令表时忽略「整段宏」类 PGP 目标，避免把 <c>^C^C…</c> 误当作命令名（含历史图层宏等）。
    /// </summary>
    internal static bool ShouldIgnorePgpTargetForCommandCatalog(string? rawTarget)
    {
        var t = (rawTarget ?? "").Trim();
        if (t.StartsWith("*", StringComparison.Ordinal))
            t = t.TrimStart('*').Trim();
        return IsScrapLayerHotkeyMacroDefinition(t);
    }

    /// <summary>
    /// 优先读取 <see cref="C_toolsPaths.UserAcadPgpPath"/>；不存在时再 <c>FindFile("acad.pgp")</c> 供命令表扫描。
    /// </summary>
    internal static bool TryReadAcadPgp(Document doc, out string path, out string content)
    {
        path = "";
        content = "";
        try
        {
            CadPgpSupportPath.EnsureC_toolsSupportFirst();
            if (File.Exists(C_toolsPaths.UserAcadPgpPath))
            {
                path = C_toolsPaths.UserAcadPgpPath;
                content = File.ReadAllText(path, DetectEncoding(path));
                return true;
            }

            var p = HostApplicationServices.Current.FindFile("acad.pgp", doc.Database, FindFileHint.Default);
            if (string.IsNullOrWhiteSpace(p) || !File.Exists(p))
                return false;
            path = p;
            content = File.ReadAllText(p, DetectEncoding(p));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析任意文本片段中的 alias,*TARGET 行（不含注释行）。
    /// </summary>
    internal static List<PgpAliasDto> ParsePgpAliasLinesFromText(string text)
    {
        var list = new List<PgpAliasDto>();
        var lines = text.Split(NewlineSeparators, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal))
                continue;
            var macroLike = line.IndexOf('^') >= 0;
            if (!macroLike)
            {
                var semi = line.IndexOf(';');
                if (semi >= 0)
                    line = line[..semi].Trim();
            }

            var comma = line.IndexOf(',');
            if (comma <= 0)
                continue;
            var alias = NormalizeAlias(line[..comma]);
            var rest = line[(comma + 1)..].Trim();
            if (rest.Length == 0)
                continue;
            if (rest.StartsWith("*", StringComparison.Ordinal))
                rest = rest[1..].Trim();
            string targetRaw;
            if (macroLike)
            {
                var cmt = rest.IndexOf(" ;", StringComparison.Ordinal);
                targetRaw = cmt >= 0 ? rest[..cmt].Trim() : rest;
            }
            else
            {
                var space = rest.IndexOfAny([' ', '\t']);
                targetRaw = space > 0 ? rest[..space] : rest;
            }

            var target = NormalizeTarget(targetRaw);
            if (alias.Length > 0 && target.Length > 0)
                list.Add(new PgpAliasDto { Alias = alias, Target = target });
        }

        return list;
    }

    /// <summary>
    /// 解析整份 PGP（含 C_TOOL 块与其它别名）。
    /// </summary>
    internal static List<PgpAliasDto> ParsePgpAliasLines(string fileContent) =>
        ParsePgpAliasLinesFromText(fileContent);

    /// <summary>
    /// 仅解析当前 acad.pgp 中 C_TOOL 管理块内的别名行，用于回填「C_TOOL 别名」列。
    /// </summary>
    internal static List<PgpAliasDto> ParseC_toolsBlockAliases(string fileContent)
    {
        var i = fileContent.IndexOf(BeginMarker, StringComparison.Ordinal);
        if (i < 0)
            return new List<PgpAliasDto>();
        i += BeginMarker.Length;
        var j = fileContent.IndexOf(EndMarker, i, StringComparison.Ordinal);
        if (j < 0)
            return new List<PgpAliasDto>();
        return ParsePgpAliasLinesFromText(fileContent[i..j])
            .Where(x => !IsRetiredLauncherAliasEntry(x.Alias, x.Target))
            .Where(x => !IsLegacyLayerShortcutPgpMacro(NormalizeTarget(x.Target)))
            .ToList();
    }

    /// <summary>
    /// 将已解析的 C_TOOL 块别名写回行的 <see cref="CommandCatalogRow.Alias"/>（按命令聚合，逗号分隔）。
    /// </summary>
    internal static void FillAliasColumn(IEnumerable<CommandCatalogRow> rows, string fullPgpContent)
    {
        var cad = ParseC_toolsBlockAliases(fullPgpContent);
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in cad)
        {
            var t = NormalizeTarget(x.Target);
            if (t.Length == 0)
                continue;
            if (!map.TryGetValue(t, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                map[t] = set;
            }

            var a = x.Alias ?? "";
            if (a.Length > 0)
                set.Add(a);
        }

        foreach (var row in rows)
        {
            if (map.TryGetValue(row.CommandName, out var set) && set.Count > 0)
                row.SetExplicitAliasFromCatalog(string.Join(", ", set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
            else
                row.SetExplicitAliasFromCatalog("");
        }
    }

    internal static Encoding DetectEncoding(string path)
    {
        using var fs = File.OpenRead(path);
        var bom = new byte[4];
        var n = fs.Read(bom, 0, 4);
        if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return new UTF8Encoding(true);
        if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return Encoding.Unicode;
        if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return Encoding.BigEndianUnicode;

        return Encoding.Default;
    }
}
