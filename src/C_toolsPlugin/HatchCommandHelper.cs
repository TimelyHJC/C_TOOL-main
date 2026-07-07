using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>统一处理 Hatch 命令启动逻辑。</summary>
internal static class HatchCommandHelper
{
    private static readonly string[] s_hatchLaunchStateVariables =
    {
        SystemVariableNames.CmdEcho,
        SystemVariableNames.FileDia,
        SystemVariableNames.HpName,
        SystemVariableNames.HpScale,
        SystemVariableNames.HpAngle,
        SystemVariableNames.HpQuickPreview,
        SystemVariableNames.HpOriginMode
    };
    private static readonly string[] s_uiStateVariables =
    {
        SystemVariableNames.FileDia,
        SystemVariableNames.CmdEcho
    };

    /// <summary>
    /// 从 F_Hatch 命令启动 Hatch（LISP CADPLUS-HATCH-START → USERS2-5 → F_Hatch）。
    /// </summary>
    internal static void StartHatchWithLayer(
        Document doc,
        string layerName,
        HatchStyleSnapshot snap)
    {
        // 准备参数
        var pattern = string.IsNullOrWhiteSpace(snap.PatternName)
            ? C_toolsConstants.DefaultHatchPattern
            : snap.PatternName.Trim();

        var scale = snap.Scale;
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            scale = C_toolsConstants.DefaultHatchScale;

        // 先保存系统变量，异常时完整回滚，正常时仅恢复 UI 相关状态。
        var savedState = CadSystemVariableService.Capture(s_hatchLaunchStateVariables);

        try
        {
            // 步骤1：关闭回显和文件对话框
            AcAp.SetSystemVariable(SystemVariableNames.CmdEcho, 0);
            AcAp.SetSystemVariable(SystemVariableNames.FileDia, 0);

            // 步骤2：确保图层存在并设为当前层
            LayerApplyService.SetCurrentLayerOnly(doc, new LayerShortcutEntry { LayerName = layerName });

            // 步骤3：设置 HP* 系统变量
            HatchCommandSystemVariableService.Apply(pattern, scale, snap.AngleDegrees);

            // 步骤4：启动 HATCH
            doc.SendStringToExecute(CommandNames.Hatch + "\n", true, false, false);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("启动 Hatch 失败，准备恢复系统状态", ex);
            savedState.TryRestoreAll();
            throw;
        }
        finally
        {
            savedState.TryRestore(s_uiStateVariables);
        }
    }
}
