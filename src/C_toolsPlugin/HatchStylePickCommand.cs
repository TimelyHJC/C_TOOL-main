using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsShared;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static class HatchStylePickCommand
{
    internal static void Run()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            HatchStylePickSession.RestorePanelIfHidden();
            return;
        }

        if (!HatchStylePickSession.HasPendingContext())
        {
            doc.Editor.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}请在配置面板「图层命令」中点某一行的「拾取…」后再选填充。");
            return;
        }

        var ed = doc.Editor;
        var peo = new PromptEntityOptions("\n选择要记录样式的填充图元: ")
        {
            AllowNone = true
        };
        peo.SetRejectMessage("\n只能选 HATCH 填充。");
        peo.AddAllowedClass(typeof(Hatch), true);

        var per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK)
        {
            ed.WriteMessage($"\n{UIMessages.Command.CancelPickStyle}");
            HatchStylePickSession.RestorePanelIfHidden();
            return;
        }

        try
        {
            var result = CadDatabaseScope.Read(
                doc,
                (_, tr) =>
                {
                    if (!CadDatabaseScope.TryOpenAs<Hatch>(tr, per.ObjectId, OpenMode.ForRead, out var hatch) ||
                        hatch == null)
                    {
                        return (Snapshot: (HatchStyleSnapshot?)null, ErrorMessage: "所选对象不是填充。");
                    }

                    return (Snapshot: HatchStyleSnapshot.FromHatch(hatch), ErrorMessage: "");
                },
                requireDocumentLock: true);

            if (result.Snapshot == null)
            {
                ed.WriteMessage("\n" + result.ErrorMessage);
                HatchStylePickSession.RestorePanelIfHidden();
                return;
            }

            HatchStylePickSession.Complete(result.Snapshot);
            ed.WriteMessage($"\n已记录填充样式: {result.Snapshot.FormatDisplay()}");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("拾取填充样式失败", ex);
            ed.WriteMessage($"\n读取填充失败: {ex.Message}");
            HatchStylePickSession.RestorePanelIfHidden();
        }
    }
}
