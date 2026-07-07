using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace C_toolsPlugin;

/// <summary>AutoCAD 用户配置 <c>.arg</c>（REGEDIT4），从 <c>Profiles\*\General</c> 与 <c>General Configuration</c> 提取可映射到系统变量的项。</summary>
internal static class CadSysConfigArg
{
    private static readonly Regex LineDword =
        new(@"^""([^""]+)""=dword:([0-9a-fA-F]{1,8})\s*$", RegexOptions.Compiled);

    private static readonly Regex LineString =
        new(@"^""([^""]+)""=""(.*)""\s*$", RegexOptions.Compiled);

    private static readonly Regex ExcludedRegistryKeyPattern =
        new(
            "Path|Dir|Folder|Template|Menu|MRU|ProfileStorage|ACADHELP|ACADLOG|DefaultFormat|NewTabPage|Registered|Author|ToolPalette|PlotLog|DGNMAPPING|ACTREC|Database|ColorBook|Enterprise|NetLocation|IconFile|TempDirectory|PlotSpooler|IESWEB|Dwfx|AppLog|ACADDRV|ACET|SheetSet|Alternative|Printer|PlotStyle|DatabaseWorkSpace|ACTRECPATH|AVEMAPS|WSCURRENT|LayerPMode|CustomColors|MaxDwg|LineWeight|UseStartUp|MTextJig",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, string> RegKeyToSysVar =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["WhipThreadEnable"] = "WHIPTHREAD",
            ["VtEnable"] = "VTENABLE",
            ["VtFps"] = "VTFPS",
            ["SelectionPreview"] = "SELECTIONPREVIEW",
            ["HatchPatternAssociativity"] = "HPASSOC",
            ["HatchGapTolerance"] = "HPGAPTOL",
            ["Osmode"] = "OSMODE",
            ["Coords"] = "COORDS",
            ["PeditAccept"] = "PEDITACCEPT",
            ["BackgroundPlot"] = "BACKGROUNDPLOT",
            ["ShowProxyDialog"] = "PROXYNOTICE",
            ["ShowProxyGraphics"] = "PROXYGRAPHICS",
            ["IsavePercent"] = "ISAVEPERCENT",
            ["ThumbSave"] = "THUMBSAVE",
        };

    private static bool IsProfileGeneralSection(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("[HKEY_", StringComparison.Ordinal) ||
            t.IndexOf("\\Profiles\\", StringComparison.Ordinal) < 0)
            return false;
        return t.EndsWith("\\General]", StringComparison.Ordinal);
    }

    private static bool IsGeneralConfigurationSection(string line)
    {
        var t = line.Trim();
        if (!t.StartsWith("[HKEY_", StringComparison.Ordinal) ||
            t.IndexOf("\\Profiles\\", StringComparison.Ordinal) < 0)
            return false;
        return t.EndsWith("\\General Configuration]", StringComparison.Ordinal);
    }

    /// <summary>从 .arg 注册表节路径中取出第一个用户配置名（排除 FixedProfile），供导入为当前配置。</summary>
    internal static bool TryGetFirstProfileName(string argPath, [NotNullWhen(true)] out string? profileName)
    {
        profileName = null;
        try
        {
            var text = ReadAllTextAuto(argPath);
            var lineStart = 0;
            while (TryReadNextLine(text, ref lineStart, out var raw))
            {
                var line = raw.Trim();
                if (line.Length < 12 || line[0] != '[')
                    continue;
                var marker = "\\Profiles\\";
                var idx = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    continue;
                var start = idx + marker.Length;
                var end = line.IndexOf('\\', start);
                if (end <= start)
                    continue;
                var name = line[start..end].Trim();
                if (name.Length == 0)
                    continue;
                if (string.Equals(name, "FixedProfile", StringComparison.OrdinalIgnoreCase))
                    continue;
                profileName = name;
                return true;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    internal static List<SysConfigRow> LoadRows(string path)
    {
        var text = ReadAllTextAuto(path);
        var list = new List<SysConfigRow>(128);
        var inSection = false;
        var sectionLabel = "";

        var lineStart = 0;
        while (TryReadNextLine(text, ref lineStart, out var raw))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
                continue;

            if (line[0] == '[')
            {
                inSection = IsProfileGeneralSection(line) || IsGeneralConfigurationSection(line);
                sectionLabel = IsProfileGeneralSection(line)
                    ? "[General]"
                    : IsGeneralConfigurationSection(line)
                        ? "[General Configuration]"
                        : "";
                continue;
            }

            if (!inSection)
                continue;

            var mDw = LineDword.Match(line);
            if (mDw.Success)
            {
                var regKey = mDw.Groups[1].Value;
                if (!ShouldIncludeRegistryKey(regKey))
                    continue;
                var hex = mDw.Groups[2].Value;
                if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var u))
                    continue;
                var sysVar = ResolveSysVarName(regKey);
                var dec = u.ToString(CultureInfo.InvariantCulture);
                var comment = $".arg {sectionLabel}；注册表 {regKey}";
                list.Add(new SysConfigRow(sysVar, dec, comment, regKey, argIsDword: true));
                continue;
            }

            var mStr = LineString.Match(line);
            if (mStr.Success)
            {
                var regKey = mStr.Groups[1].Value;
                if (!ShouldIncludeRegistryKey(regKey))
                    continue;
                var val = UnescapeRegString(mStr.Groups[2].Value);
                if (val.Length > 64 || val.Contains('%') || (val.Contains('\\') && val.Length > 32))
                    continue;
                var sysVar = ResolveSysVarName(regKey);
                var comment = $".arg {sectionLabel}；注册表 {regKey}";
                list.Add(new SysConfigRow(sysVar, val, comment, regKey, argIsDword: false));
            }
        }

        return list;
    }

    private static bool TryReadNextLine(string text, ref int lineStart, out string line)
    {
        var length = text.Length;
        if (lineStart >= length)
        {
            line = "";
            return false;
        }

        var lineEnd = lineStart;
        while (lineEnd < length && text[lineEnd] != '\r' && text[lineEnd] != '\n')
            lineEnd++;

        line = text.Substring(lineStart, lineEnd - lineStart);
        if (lineEnd < length && text[lineEnd] == '\r' && lineEnd + 1 < length && text[lineEnd + 1] == '\n')
            lineEnd++;

        lineStart = lineEnd + 1;
        return true;
    }

    private static bool ShouldIncludeRegistryKey(string key)
    {
        if (key.Length == 0 || key.Length > 48)
            return false;
        if (ExcludedRegistryKeyPattern.IsMatch(key))
            return false;
        return true;
    }

    private static string ResolveSysVarName(string regKey) =>
        RegKeyToSysVar.TryGetValue(regKey, out var s) ? s : regKey.ToUpperInvariant();

    private static string UnescapeRegString(string s) => s.Replace(@"\""", "\"");

    internal static void SaveToPath(string destPath, IReadOnlyList<SysConfigRow> rows, string templatePath)
    {
        var basePath = File.Exists(destPath) ? destPath : templatePath;
        if (!File.Exists(basePath))
            throw new IOException("缺少 .arg 模板：" + templatePath);

        var s = ReadAllTextAuto(basePath);
        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.ArgRegistryKey))
                continue;

            var keyEsc = Regex.Escape(r.ArgRegistryKey);
            if (r.ArgIsDword)
            {
                if (!long.TryParse(r.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var li) ||
                    li < 0 || li > uint.MaxValue)
                    continue;
                var hex = ((uint)li).ToString("x8", CultureInfo.InvariantCulture);
                var pattern = "(\"" + keyEsc + "\"=dword:)([0-9a-fA-F]{1,8})(\\s*)";
                s = Regex.Replace(s, pattern, "${1}" + hex + "${3}",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }
            else
            {
                var esc = r.Value.Replace("\\", @"\\").Replace("\"", "\\\"");
                var pattern = "(\"" + keyEsc + "\"=\")([^\"]*)(\")";
                s = Regex.Replace(s, pattern, "${1}" + esc + "${3}", RegexOptions.Multiline);
            }
        }

        WriteAllTextAuto(destPath, s, basePath);
    }

    internal static string ReadAllTextAuto(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.UTF8.GetString(bytes);
    }

    private static void WriteAllTextAuto(string path, string content, string templatePath)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tplBytes = File.ReadAllBytes(templatePath);
        if (tplBytes.Length >= 2 && tplBytes[0] == 0xFF && tplBytes[1] == 0xFE)
        {
            using var fs = File.Create(path);
            fs.Write(new byte[] { 0xFF, 0xFE }, 0, 2);
            var unicode = Encoding.Unicode.GetBytes(content);
            fs.Write(unicode, 0, unicode.Length);
        }
        else
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
