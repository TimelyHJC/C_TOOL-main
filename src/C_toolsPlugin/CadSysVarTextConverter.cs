using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>将系统变量与界面文本互转，并执行 <see cref="AcAp.SetSystemVariable"/>。</summary>
internal static class CadSysVarTextConverter
{
    internal static string FormatValue(object? v)
    {
        if (v == null)
            return "";

        return v switch
        {
            bool b => b ? "1" : "0",
            int i => i.ToString(CultureInfo.InvariantCulture),
            short s => s.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("G", CultureInfo.InvariantCulture),
            float f => f.ToString("G", CultureInfo.InvariantCulture),
            string s => s,
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? ""
        };
    }

    internal static bool TryReadVar(string name, out string text, out string? error)
    {
        text = "";
        error = null;
        if (CadDisplayPreferenceColors.IsDisplayColorKey(name))
            return CadDisplayPreferenceColors.TryRead(name, out text, out error);

        if (!CadSystemVariableService.TryGetValue(name, out var value, out error))
            return false;

        text = FormatValue(value);
        return true;
    }

    internal static bool TryWriteVar(string name, string userText, out string? error)
    {
        error = null;
        var t = (userText ?? "").Trim();

        if (CadDisplayPreferenceColors.IsDisplayColorKey(name))
            return CadDisplayPreferenceColors.TryWrite(name, t, out error);

        try
        {
            if (long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var li) &&
                li >= int.MinValue && li <= int.MaxValue)
            {
                var iv = (int)li;
                // 多数整型系统变量在 ObjectARX 中为 Int16；传 int 可能触发 eInvalidInput
                if (iv >= short.MinValue && iv <= short.MaxValue)
                {
                    if (CadSystemVariableService.TrySetValue(name, (short)iv, out error))
                        return true;
                }

                return CadSystemVariableService.TrySetValue(name, iv, out error);
            }

            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                !double.IsNaN(d) && !double.IsInfinity(d))
            {
                return CadSystemVariableService.TrySetValue(name, d, out error);
            }

            return CadSystemVariableService.TrySetValue(name, t, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
