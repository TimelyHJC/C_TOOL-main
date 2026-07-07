using System.Globalization;
using System.Reflection;
using System.Windows.Media;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>
/// 读写「选项 → 显示 → 颜色」中绘图窗口背景（非系统变量，走 Preferences.Display COM 属性）。
/// 表格中变量名以 DSP_ 开头；值格式：R,G,B（0～255）或 OLE_COLOR 整数。
/// </summary>
internal static class CadDisplayPreferenceColors
{
    private static readonly Dictionary<string, string[]> KeyToFixedCandidates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DSP_MODEL2D_BG"] = new[] { "GraphicsWinModelBackgrndColor" },
        ["DSP_LAYOUT_BG"] = new[] { "GraphicsWinLayoutBackgrndColor" },
        ["DSP_UNIFIED_BG"] = new[]
        {
            "GraphicsWinModelUniformBackgrndColor",
            "GraphicsWinTextBackgrndColor"
        },
        ["DSP_BLOCKEDIT_BG"] = new[]
        {
            "GraphicsWinBlockEditorBackgrndColor",
            "GraphicsWinBEditBackgrndColor"
        }
    };

    private static string? _cachedUniformPropRead;
    private static string? _cachedUniformPropWrite;
    private static string? _cachedBlockEditProp;

    internal static bool IsDisplayColorKey(string name) => KeyToFixedCandidates.ContainsKey(name);

    /// <summary>将单元格中的 R,G,B 或 OLE 整数解析为 WPF 颜色（用于「值」列色块）。</summary>
    internal static bool TryGetSwatchColor(string varName, string valueText, out Color color)
    {
        color = default;
        if (!IsDisplayColorKey(varName))
            return false;
        if (!TryParseColorInput(valueText, out var ole))
            return false;
        var r = ole & 0xFF;
        var g = (ole >> 8) & 0xFF;
        var b = (ole >> 16) & 0xFF;
        color = Color.FromRgb((byte)r, (byte)g, (byte)b);
        return true;
    }

    internal static bool TryRead(string name, out string text, out string? error)
    {
        text = "";
        error = null;
        if (!KeyToFixedCandidates.TryGetValue(name, out var fixedList))
            return false;

        try
        {
            dynamic prefs = AcAp.Preferences;
            object disp = prefs.Display;
            foreach (var propName in EnumerateReadCandidates(disp, name, fixedList))
            {
                if (TryGetOleColorProperty(disp, propName, out var ole))
                {
                    text = OleColorToRgbString(ole);
                    return true;
                }
            }

            error = "未找到可用的显示颜色首选项属性（可能当前版本接口不同）。";
            return false;
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryWrite(string name, string userText, out string? error)
    {
        error = null;
        if (!KeyToFixedCandidates.TryGetValue(name, out var fixedList))
            return false;

        if (!TryParseColorInput(userText, out var ole))
        {
            error = "颜色格式：R,G,B（0～255，逗号或空格分隔）或单个 OLE_COLOR 整数。";
            return false;
        }

        try
        {
            dynamic prefs = AcAp.Preferences;
            object disp = prefs.Display;
            foreach (var propName in EnumerateWriteCandidates(disp, name, fixedList))
            {
                if (TrySetOleColorProperty(disp, propName, ole))
                    return true;
            }

            error = "无法写入该显示颜色（属性不存在或只读）。";
            return false;
        }
        catch (System.Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateReadCandidates(object dispObj, string key, string[] fixedList)
    {
        if (string.Equals(key, "DSP_UNIFIED_BG", StringComparison.Ordinal))
        {
            var u = ResolveUniformPropertyName(dispObj);
            if (u != null)
                yield return u;
        }
        else if (string.Equals(key, "DSP_BLOCKEDIT_BG", StringComparison.Ordinal))
        {
            var b = ResolveBlockEditorPropertyName(dispObj);
            if (b != null)
                yield return b;
        }

        foreach (var n in fixedList)
            yield return n;
    }

    private static IEnumerable<string> EnumerateWriteCandidates(object dispObj, string key, string[] fixedList)
    {
        // 写入与读取分开：统一背景等项在部分版本上存在「只读」同名属性，须优先可写属性
        if (string.Equals(key, "DSP_UNIFIED_BG", StringComparison.Ordinal))
        {
            var uw = ResolveUniformPropertyNameForWrite(dispObj);
            if (uw != null)
                yield return uw;
            foreach (var n in fixedList)
                yield return n;
            yield break;
        }

        if (string.Equals(key, "DSP_BLOCKEDIT_BG", StringComparison.Ordinal))
        {
            var bw = ResolveBlockEditorPropertyNameForWrite(dispObj);
            if (bw != null)
                yield return bw;
            foreach (var n in fixedList)
                yield return n;
            yield break;
        }

        foreach (var n in EnumerateReadCandidates(dispObj, key, fixedList))
            yield return n;
    }

    private static string? ResolveUniformPropertyName(object dispObj)
    {
        if (_cachedUniformPropRead != null &&
            dispObj.GetType().GetProperty(_cachedUniformPropRead, BindingFlags.Public | BindingFlags.Instance) != null)
            return _cachedUniformPropRead;

        foreach (var p in dispObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var n = p.Name;
            if (n.IndexOf("Uniform", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (n.IndexOf("Backgrnd", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("Background", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!IsReadableColorProperty(p))
                continue;
            _cachedUniformPropRead = n;
            return n;
        }

        return null;
    }

    /// <summary>仅用于写入：必须 <see cref="PropertyInfo.CanWrite"/>，避免命中只读 COM 属性导致全部失败。</summary>
    private static string? ResolveUniformPropertyNameForWrite(object dispObj)
    {
        if (_cachedUniformPropWrite != null)
        {
            var p0 = dispObj.GetType().GetProperty(_cachedUniformPropWrite, BindingFlags.Public | BindingFlags.Instance);
            if (p0 != null && IsWritableColorProperty(p0))
                return _cachedUniformPropWrite;
        }

        foreach (var p in dispObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var n = p.Name;
            if (n.IndexOf("Uniform", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (n.IndexOf("Backgrnd", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("Background", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!IsWritableColorProperty(p))
                continue;
            _cachedUniformPropWrite = n;
            return n;
        }

        return null;
    }

    private static string? ResolveBlockEditorPropertyName(object dispObj)
    {
        if (_cachedBlockEditProp != null &&
            dispObj.GetType().GetProperty(_cachedBlockEditProp, BindingFlags.Public | BindingFlags.Instance) != null)
            return _cachedBlockEditProp;

        foreach (var p in dispObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var n = p.Name;
            var hasBlock = n.IndexOf("Block", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           n.IndexOf("BEdit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           n.IndexOf("Bedit", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasBlock)
                continue;
            if (n.IndexOf("Backgrnd", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("Background", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!IsReadableColorProperty(p))
                continue;
            _cachedBlockEditProp = n;
            return n;
        }

        return null;
    }

    private static string? ResolveBlockEditorPropertyNameForWrite(object dispObj)
    {
        foreach (var p in dispObj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var n = p.Name;
            var hasBlock = n.IndexOf("Block", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           n.IndexOf("BEdit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                           n.IndexOf("Bedit", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasBlock)
                continue;
            if (n.IndexOf("Backgrnd", StringComparison.OrdinalIgnoreCase) < 0 &&
                n.IndexOf("Background", StringComparison.OrdinalIgnoreCase) < 0)
                continue;
            if (!IsWritableColorProperty(p))
                continue;
            return n;
        }

        return null;
    }

    private static bool IsReadableColorProperty(PropertyInfo p)
    {
        if (!p.CanRead)
            return false;
        var t = p.PropertyType;
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(uint);
    }

    private static bool IsWritableColorProperty(PropertyInfo p)
    {
        if (!p.CanWrite)
            return false;
        var t = p.PropertyType;
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(uint);
    }

    private static bool TryGetOleColorProperty(object dispObj, string propName, out int ole)
    {
        ole = 0;
        var t = dispObj.GetType();
        try
        {
            var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead)
            {
                var v = p.GetValue(dispObj);
                if (v != null)
                {
                    ole = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                    return true;
                }
            }
        }
        catch
        {
            // fall through to COM InvokeMember
        }

        try
        {
            var v = t.InvokeMember(propName, BindingFlags.GetProperty, null, dispObj, null, CultureInfo.InvariantCulture);
            if (v != null)
            {
                ole = Convert.ToInt32(v, CultureInfo.InvariantCulture);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool TrySetOleColorProperty(object dispObj, string propName, int ole)
    {
        var t = dispObj.GetType();
        try
        {
            var p = t.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite)
            {
                var targetType = p.PropertyType;
                object boxed = targetType == typeof(long)
                    ? (long)ole
                    : targetType == typeof(short)
                        ? (short)ole
                        : targetType == typeof(uint)
                            ? unchecked((uint)ole)
                            : ole;
                p.SetValue(dispObj, boxed);
                return true;
            }
        }
        catch
        {
            // fall through to COM InvokeMember
        }

        try
        {
            t.InvokeMember(propName, BindingFlags.SetProperty, null, dispObj, new object[] { ole }, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static string OleColorToRgbString(int ole)
    {
        var r = ole & 0xFF;
        var g = (ole >> 8) & 0xFF;
        var b = (ole >> 16) & 0xFF;
        return r.ToString(CultureInfo.InvariantCulture) + "," +
               g.ToString(CultureInfo.InvariantCulture) + "," +
               b.ToString(CultureInfo.InvariantCulture);
    }

    private static bool TryParseColorInput(string userText, out int ole)
    {
        ole = 0;
        var t = (userText ?? "").Trim();
        if (t.Length == 0)
            return false;

        var parts = t.Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 3 &&
            int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) &&
            int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) &&
            int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            r = Clamp255(r);
            g = Clamp255(g);
            b = Clamp255(b);
            ole = r | (g << 8) | (b << 16);
            return true;
        }

        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
        {
            ole = single;
            return true;
        }

        return false;
    }

    private static int Clamp255(int v)
    {
        if (v < 0)
            return 0;
        if (v > 255)
            return 255;
        return v;
    }
}
