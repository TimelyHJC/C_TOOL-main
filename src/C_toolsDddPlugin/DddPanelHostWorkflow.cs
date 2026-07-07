using System;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddPanelHostWorkflow
{
    internal static void ShowPanelDialog(Document doc)
    {
        var editor = doc.Editor;
        DddDrawingSelectionSync.CaptureImpliedTextSelection(doc);
        var initialInputText = DddDrawingSelectionSync.TryGetSelectedTextContents(doc);

        try
        {
            var window = new DddPanelWindow(initialInputText);
            _ = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                window,
                false);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开文字标注弹窗失败（无效操作）", ex);
            editor.WriteMessage("\nC_TOOL：文字标注窗口打开失败：" + ex.Message);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开文字标注弹窗失败（CAD）", ex);
            editor.WriteMessage("\nC_TOOL：文字标注窗口打开失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开文字标注弹窗失败", ex);
            editor.WriteMessage("\nC_TOOL：文字标注窗口打开失败：" + ex.Message);
        }
        finally
        {
            DddDrawingSelectionSync.ClearCapturedTextSelection(doc);
        }
    }

    internal static void ClosePanelIfAny(Document? doc)
    {
        if (doc != null)
            DddDrawingSelectionSync.ClearCapturedTextSelection(doc);
    }
}
