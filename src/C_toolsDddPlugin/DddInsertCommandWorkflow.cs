using System;
using Autodesk.AutoCAD.ApplicationServices;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddInsertCommandWorkflow
{
    internal static void RunLeaderInsert(Document doc, string commandId)
    {
        TryApplyConfiguredMLeaderStyle(doc, commandId);
        DddLeaderInsertService.RunInteractiveLeader(doc);
    }

    internal static void RunTextInsert(Document doc, string commandId)
    {
        TryApplyConfiguredMLeaderStyle(doc, commandId);
        DddLeaderInsertService.RunInteractiveText(doc);
    }

    private static void TryApplyConfiguredMLeaderStyle(Document doc, string commandId)
    {
        var pendingStyleName = DddLeaderInsertService.TakePendingMLeaderStyle(doc);
        if (TryApplyStyleName(doc, pendingStyleName, $"{commandId} 应用面板选中多重引线样式"))
            return;

        var configuredStyleName = UserConfigurationStore.TryGetMLeaderStyleName(commandId);
        _ = TryApplyStyleName(doc, configuredStyleName, $"{commandId} 应用 Configuration 多重引线样式");
    }

    private static bool TryApplyStyleName(Document doc, string? configuredStyleName, string operationName)
    {
        if (configuredStyleName == null || string.IsNullOrWhiteSpace(configuredStyleName))
            return false;
        var styleName = configuredStyleName.Trim();

        try
        {
            using (doc.LockDocument())
            {
                if (!MLeaderStyleHelper.TryGetMLeaderStyleObjectId(doc.Database, styleName, out var styleId) || styleId.IsNull)
                    return false;

                var currentStyleName = MLeaderStyleHelper.TryGetCurrentStyleName(doc);
                if (string.Equals(currentStyleName, styleName, StringComparison.OrdinalIgnoreCase))
                    return true;

                return MLeaderStyleHelper.TrySetCurrentStyleName(doc, styleName);
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}失败（CAD）", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{operationName}失败（参数）", ex);
        }

        return false;
    }
}
