using System;
using System.Collections.Generic;
using System.Globalization;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsShared;

/// <summary>
/// AutoCAD 系统变量名称常量。
/// </summary>
public static class SystemVariableNames
{
    public const string Users1 = "USERS1";
    public const string Users2 = "USERS2";
    public const string Users3 = "USERS3";
    public const string Users4 = "USERS4";
    public const string Users5 = "USERS5";
    public const string TextSize = "TEXTSIZE";
    public const string Insunits = "INSUNITS";
    public const string HpName = "HPNAME";
    public const string HpScale = "HPSCALE";
    public const string HpAngle = "HPANG";
    public const string HpQuickPreview = "HPQUICKPREVIEW";
    public const string HpOriginMode = "HPORIGINMODE";
    public const string LtScale = "LTSCALE";
    public const string PsLtScale = "PSLTSCALE";
    public const string FileDia = "FILEDIA";
    public const string CmdEcho = "CMDECHO";
    public const string Cannoscale = "CANNOSCALE";
    public const string CvPort = "CVPORT";
    public const string Clayer = "CLAYER";
    public const string MirrText = "MIRRTEXT";
    public const string CurrentMLeaderStyle = "CMLEADERSTYLE";
    public const string Acad = "ACAD";
    public const string WsCurrent = "WSCURRENT";
}

/// <summary>
/// 系统变量安全访问辅助器，统一读写错误处理与日志输出。
/// </summary>
public static class CadSystemVariableService
{
    /// <summary>
    /// 安全读取系统变量原始值。
    /// </summary>
    public static object? TryGetValue(string name)
    {
        return TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// 安全读取系统变量原始值。
    /// </summary>
    public static bool TryGetValue(string name, out object? value)
    {
        return TryGetValueCore(name, out value, logFailure: true, out _);
    }

    /// <summary>
    /// 安全读取系统变量原始值，并返回错误消息。
    /// </summary>
    public static bool TryGetValue(string name, out object? value, out string? error)
    {
        return TryGetValueCore(name, out value, logFailure: true, out error);
    }

    /// <summary>
    /// 安全读取字符串系统变量。
    /// </summary>
    public static string? TryGetString(string name)
    {
        return TryGetValue(name, out var value, out _) ? value as string : null;
    }

    /// <summary>
    /// 安全读取并修剪字符串系统变量，空白时返回默认值。
    /// </summary>
    public static string GetTrimmedStringOrDefault(string name, string defaultValue = "")
    {
        var value = TryGetString(name);
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? defaultValue : trimmed!;
    }

    /// <summary>
    /// 安全写入系统变量。
    /// </summary>
    public static bool TrySetValue(string name, object value)
    {
        return TrySetValueCore(name, value, logFailure: true, out _);
    }

    /// <summary>
    /// 安全写入系统变量，并返回错误消息。
    /// </summary>
    public static bool TrySetValue(string name, object value, out string? error)
    {
        return TrySetValueCore(name, value, logFailure: true, out error);
    }

    /// <summary>
    /// 捕获一组系统变量，供后续统一恢复。
    /// </summary>
    public static CadSystemVariableSnapshot Capture(params string[] names)
    {
        var capturedValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var failedNames = new List<string>();

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name) || capturedValues.ContainsKey(name))
                continue;

            if (TryGetValueCore(name, out var value, logFailure: false, out _))
            {
                capturedValues[name] = value;
            }
            else
            {
                failedNames.Add(name);
            }
        }

        if (failedNames.Count > 0)
        {
            C_toolsDiagnostics.LogNonFatal(
                $"捕获系统变量失败：{string.Join(", ", failedNames)}",
                null);
        }

        return new CadSystemVariableSnapshot(capturedValues);
    }

    /// <summary>
    /// 安全读取正数型系统变量。
    /// </summary>
    public static bool TryGetPositiveDouble(string name, out double value)
    {
        value = 0.0;
        if (!TryGetValue(name, out var raw) || !TryConvertToDouble(raw, out value))
            return false;

        return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;
    }

    /// <summary>
    /// 安全读取 Int32 系统变量。
    /// </summary>
    public static bool TryGetInt32(string name, out int value)
    {
        value = 0;
        if (!TryGetValue(name, out var raw))
            return false;

        try
        {
            value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 批量清空字符串系统变量。
    /// </summary>
    public static void ClearStrings(params string[] names)
    {
        foreach (var name in names)
        {
            if (!string.IsNullOrWhiteSpace(name))
                TrySetValue(name, "");
        }
    }

    internal static bool TryGetValueCore(string name, out object? value, bool logFailure, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("系统变量名不能为空。", nameof(name));

        try
        {
            value = AcAp.GetSystemVariable(name);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            if (logFailure)
                C_toolsDiagnostics.LogNonFatal($"读取系统变量 {name} 失败（{ex.GetType().Name}）", ex);

            error = ex.Message;
            value = null;
            return false;
        }
    }

    internal static bool TrySetValueCore(string name, object value, bool logFailure, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("系统变量名不能为空。", nameof(name));

        try
        {
            AcAp.SetSystemVariable(name, value);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            if (logFailure)
                C_toolsDiagnostics.LogNonFatal($"设置系统变量 {name} 失败（{ex.GetType().Name}）", ex);

            error = ex.Message;
            return false;
        }
    }

    private static bool TryConvertToDouble(object? raw, out double value)
    {
        value = 0.0;

        if (raw == null)
            return false;

        try
        {
            value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
