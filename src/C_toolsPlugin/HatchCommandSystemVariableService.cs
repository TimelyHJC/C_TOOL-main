using System;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>
/// 统一设置 Hatch 启动前需要的 HP* 系统变量。
/// </summary>
internal static class HatchCommandSystemVariableService
{
    private const short HatchQuickPreviewOn = 1;
    private const short HatchPreviewOriginCenter = 5;

    internal static void Apply(string pattern, double scale, double angleDegrees)
    {
        var angleRadians = angleDegrees * (Math.PI / 180.0);
        AcAp.SetSystemVariable(SystemVariableNames.HpName, pattern);
        AcAp.SetSystemVariable(SystemVariableNames.HpScale, scale);
        AcAp.SetSystemVariable(SystemVariableNames.HpAngle, angleRadians);
        AcAp.SetSystemVariable(SystemVariableNames.HpQuickPreview, HatchQuickPreviewOn);
        AcAp.SetSystemVariable(SystemVariableNames.HpOriginMode, HatchPreviewOriginCenter);
    }
}
