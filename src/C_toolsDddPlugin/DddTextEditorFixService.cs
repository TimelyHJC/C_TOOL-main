using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddTextEditorFixService
{
    private const string MTextEditorSysVar = "MTEXTED";
    private const string TextEditorSysVar = "TEXTED";
    private const string InternalEditorValue = "Internal";

    internal static void Run(Document doc)
    {
        var ed = doc.Editor;
        var details = new List<string>();
        var hasFailure = false;

        if (!TryRepairSystemVariable(MTextEditorSysVar, out var mtextMessage, out var mtextError))
        {
            hasFailure = true;
            details.Add($"{MTextEditorSysVar} 失败：{mtextError}");
        }
        else
        {
            details.Add(mtextMessage);
        }

        if (!TryRepairSystemVariable(TextEditorSysVar, out var textMessage, out var textError))
        {
            hasFailure = true;
            details.Add($"{TextEditorSysVar} 失败：{textError}");
        }
        else
        {
            details.Add(textMessage);
        }

        var summary = string.Join("；", details);
        ed.WriteMessage(hasFailure
            ? $"\nC_TOOL：F_TextEditFix 已部分执行。{summary}"
            : $"\nC_TOOL：F_TextEditFix 已完成。{summary}");
    }

    private static bool TryRepairSystemVariable(string name, out string message, out string error)
    {
        message = string.Empty;
        error = string.Empty;

        if (!TryGetSystemVariableText(name, out var originalValue, out error))
            return false;

        if (string.Equals(originalValue, InternalEditorValue, StringComparison.OrdinalIgnoreCase))
        {
            message = $"{name} 已是 {InternalEditorValue}";
            return true;
        }

        if (!TrySetSystemVariableText(name, InternalEditorValue, out error))
            return false;

        if (!TryGetSystemVariableText(name, out var currentValue, out error))
            return false;

        message = $"{name}：{FormatValue(originalValue)} -> {FormatValue(currentValue)}";
        return true;
    }

    private static bool TryGetSystemVariableText(string name, out string value, out string error)
    {
        value = string.Empty;
        if (!CadSystemVariableService.TryGetValue(name, out var raw, out var readError))
        {
            error = readError ?? "读取失败。";
            return false;
        }

        error = string.Empty;
        value = raw == null
            ? string.Empty
            : Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty;
        return true;
    }

    private static bool TrySetSystemVariableText(string name, string value, out string error)
    {
        if (CadSystemVariableService.TrySetValue(name, value, out var writeError))
        {
            error = string.Empty;
            return true;
        }

        error = writeError ?? "设置失败。";
        return false;
    }

    private static string FormatValue(string value) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
}
