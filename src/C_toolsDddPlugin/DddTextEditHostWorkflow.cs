using System;
using Autodesk.AutoCAD.ApplicationServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

internal static class DddTextEditHostWorkflow
{
    internal static void ShowWindowDialog(Document doc)
    {
        var editor = doc.Editor;
        var hasSelection =
            DddDrawingSelectionSync.TryCaptureSelectedTextContents(doc, out var texts, out var selectedCount) &&
            selectedCount > 0;

        if (!hasSelection)
        {
            editor.WriteMessage("\nC_TOOL：F_ED 请选择要快改的文字。");
            hasSelection =
                DddDrawingSelectionSync.TryPromptAndCaptureSelectedTextContents(doc, out texts, out selectedCount) &&
                selectedCount > 0;
        }

        if (!hasSelection)
        {
            DddDrawingSelectionSync.ClearCapturedTextSelection(doc);
            return;
        }

        try
        {
            var window = new DddTextEditWindow(texts, selectedCount);
            _ = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                window,
                false);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_ED 文字编辑弹窗失败（无效操作）", ex);
            editor.WriteMessage("\nC_TOOL：F_ED 打开失败：" + ex.Message);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_ED 文字编辑弹窗失败（CAD）", ex);
            editor.WriteMessage("\nC_TOOL：F_ED 打开失败：" + ex.Message);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("打开 F_ED 文字编辑弹窗失败", ex);
            editor.WriteMessage("\nC_TOOL：F_ED 打开失败：" + ex.Message);
        }
        finally
        {
            DddDrawingSelectionSync.ClearCapturedTextSelection(doc);
        }
    }

    internal static void CloseWindowIfAny(Document? doc)
    {
        if (doc != null)
            DddDrawingSelectionSync.ClearCapturedTextSelection(doc);
    }
}
