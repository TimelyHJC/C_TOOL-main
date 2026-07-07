using System;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsDddPlugin;

internal static class DddPanelInsertWorkflow
{
    internal static bool TryApplyListTextToDrawingSelection(string textToApply)
    {
        if (AcAp.DocumentManager.MdiActiveDocument == null)
            return false;

        var applied = false;
        try
        {
            AcAp.DocumentManager.ExecuteInApplicationContext(
                _ =>
                {
                    var doc = AcAp.DocumentManager.MdiActiveDocument;
                    if (doc == null)
                        return;

                    applied = DddDrawingSelectionSync.TryCaptureAndApplyTextToImpliedSelection(doc, textToApply);
                },
                null);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ExecuteInApplicationContext（列表写回图中文字）", ex);
        }

        return applied;
    }

    internal static bool TryQueueInsertFromPanel(
        Document doc,
        string text,
        bool useLeader,
        string? preferredMLeaderStyleName,
        out string errorMessage)
    {
        errorMessage = "";
        var value = DddTextContentHelper.NormalizeLineEndings(text);
        if (!DddTextContentHelper.HasVisibleText(value))
        {
            errorMessage = "主列文字为空。";
            return false;
        }

        try
        {
            DddDrawingSelectionSync.ClearCapturedTextSelection(doc);
            DddLeaderInsertService.SetPendingText(doc, value);
            DddLeaderInsertService.SetPendingMLeaderStyle(doc, preferredMLeaderStyleName);

            var commandName = useLeader
                ? DddPluginCommandIds.DddInsertLeader
                : DddPluginCommandIds.DddInsertText;

            doc.SendStringToExecute("_." + commandName + "\n", true, false, false);
            return true;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("触发文字标注插入命令", ex);
            DddLeaderInsertService.ClearPendingMLeaderStyle(doc);
            errorMessage = ex.Message;
            return false;
        }
    }
}
