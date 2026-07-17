using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace C_toolsDddPlugin;

internal static class DddTextToLeaderService
{
    internal static void Run(Document doc)
    {
        if (!TryResolveSelectedText(doc, out var text))
            return;

        DddLeaderInsertService.SetPendingText(doc, text);
        doc.Editor.SetImpliedSelection(Array.Empty<ObjectId>());
        DddLeaderInsertService.RunInteractiveLeader(doc);
    }

    private static bool TryResolveSelectedText(Document doc, out string text)
    {
        text = "";
        var ed = doc.Editor;

        try
        {
            var implied = ed.SelectImplied();
            if (implied.Status == PromptStatus.OK
                && implied.Value != null
                && DddReadableTextService.TryReadTextFromIds(doc, implied.Value.GetObjectIds(), out text))
            {
                return true;
            }

            var result = ed.GetNestedEntity(new PromptNestedEntityOptions("\nC_TOOL：选择要生成多重引线的文字："));
            if (result.Status != PromptStatus.OK || result.ObjectId.IsNull)
                return false;

            if (DddReadableTextService.TryReadTextFromId(doc, result.ObjectId, out text))
                return true;

            ed.WriteMessage("\nC_TOOL：所选对象没有可读取的文字。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDC 选择文字失败（无效操作）", ex);
            ed.WriteMessage("\nC_TOOL：读取文字失败。");
        }
        catch (AcRx.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDC 选择文字失败（CAD）", ex);
            ed.WriteMessage("\nC_TOOL：读取文字失败。");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_DDC 选择文字失败（参数）", ex);
            ed.WriteMessage("\nC_TOOL：读取文字失败。");
        }

        text = "";
        return false;
    }
}
