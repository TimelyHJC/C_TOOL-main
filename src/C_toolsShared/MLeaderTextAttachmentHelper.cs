using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsShared;

/// <summary>从插件 JSON 应用「连接位置-左/右」与样式「最大引线点数」。</summary>
internal static class MLeaderTextAttachmentHelper
{
    internal static void ApplySetTextAttachmentTypesFromPlugin(MLeader ml, MLeaderToolSettingsDto s)
    {
        if (!string.IsNullOrWhiteSpace(s.LeftTextAttachmentType)
            && TryParseTextAttachmentType(s.LeftTextAttachmentType, out var left))
        {
            try
            {
                ml.SetTextAttachmentType(left, LeaderDirectionType.LeftLeader);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 插件覆盖连接位置（左）", ex);
            }
        }

        if (!string.IsNullOrWhiteSpace(s.RightTextAttachmentType)
            && TryParseTextAttachmentType(s.RightTextAttachmentType, out var right))
        {
            try
            {
                ml.SetTextAttachmentType(right, LeaderDirectionType.RightLeader);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 插件覆盖连接位置（右）", ex);
            }
        }
    }

    internal static void ApplyMaxLeaderSegmentsPointsToStyle(MLeader ml, Transaction tr, int maxPoints)
    {
        if (ml == null || tr == null || ml.MLeaderStyle.IsNull || maxPoints < 2)
            return;
        try
        {
            var ms = (MLeaderStyle)tr.GetObject(ml.MLeaderStyle, OpenMode.ForWrite);
            ms.MaxLeaderSegmentsPoints = maxPoints;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("MLeaderStyle 最大引线点数（插件）", ex);
        }
    }

    private static bool TryParseTextAttachmentType(string name, out TextAttachmentType value)
    {
        value = default;
        var n = (name ?? "").Trim();
        if (n.Length == 0)
            return false;
        return Enum.TryParse(n, ignoreCase: true, out value);
    }
}
