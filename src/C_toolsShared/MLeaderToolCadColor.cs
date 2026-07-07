using System.Globalization;
using Autodesk.AutoCAD.Colors;

namespace C_toolsShared;

/// <summary>与标注批量类似：BYLAYER / BYBLOCK / 0～256 ACI。</summary>
public static class MLeaderToolCadColor
{
    public static bool TryParse(string? s, out Color color)
    {
        s = s?.Trim() ?? "";
        if (s.Equals("BYLAYER", StringComparison.OrdinalIgnoreCase))
        {
            color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            return true;
        }

        if (s.Equals("BYBLOCK", StringComparison.OrdinalIgnoreCase))
        {
            color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
            return true;
        }

        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            || int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out n))
        {
            if (n >= 0 && n <= 256)
            {
                color = Color.FromColorIndex(ColorMethod.ByAci, (short)n);
                return true;
            }
        }

        color = Color.FromColorIndex(ColorMethod.ByAci, 7);
        return false;
    }

    /// <summary>与标注批量 <c>ColorToUi</c> 一致，用于只读展示。</summary>
    public static string ToUiString(Color c)
    {
        try
        {
            if (c.ColorMethod == ColorMethod.ByLayer)
                return "BYLAYER";
            if (c.ColorMethod == ColorMethod.ByBlock)
                return "BYBLOCK";
            if (c.ColorMethod == ColorMethod.ByAci || c.ColorMethod == ColorMethod.ByColor)
                return c.ColorIndex.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            // 忽略
        }

        return "7";
    }
}
