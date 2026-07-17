using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

public class DddCommands
{
    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.Ddd, CommandFlags.Modal)]
    [CommandMethod(DddPluginCommandIds.CommandGroup, C_toolsCommandIds.Ddd.AliasShort, CommandFlags.Modal)]
    public void ToggleDddPanel()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        DddPanelHostWorkflow.ShowPanelDialog(doc);
    }

    /// <summary>由面板触发：点取箭头与文字位置后插入多重引线。</summary>
    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DddLeader, CommandFlags.Modal)]
    public void F_DddLeaderInsert()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddInsertCommandWorkflow.RunLeaderInsert(doc, DddPluginCommandIds.DddLeader);
    }

    /// <summary>由面板列表触发：优先 PendingText，否则沿用 <see cref="DddLeaderInsertService.LastInsertedText"/>。</summary>
    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DddInsertLeader, CommandFlags.Modal)]
    public void F_DddInsertLeader()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddInsertCommandWorkflow.RunLeaderInsert(doc, DddPluginCommandIds.DddInsertLeader);
    }

    /// <summary>由面板列表触发：优先 PendingText，否则沿用 <see cref="DddLeaderInsertService.LastInsertedText"/>，并按单点插入纯文字。</summary>
    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DddInsertText, CommandFlags.Modal)]
    public void F_DddInsertText()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddInsertCommandWorkflow.RunTextInsert(doc, DddPluginCommandIds.DddInsertText);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimAligned, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_DaAlignedDimension()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        CurrentDimStyleSync.TrySyncFromDatabase(doc, C_toolsCommandIds.Ddd.DimAligned, out _);
        DddAlignedDimensionService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimAlignedContinueInternal, CommandFlags.Modal | CommandFlags.NoHistory)]
    public void F_DaAlignedDimensionContinueInternal()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        if (!DddAlignedDimensionService.ContinueAfterNativeAlignedDimension(doc))
            F_DaAlignedDimension();
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimLinear, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_DcLinearDimension()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        CurrentDimStyleSync.TrySyncFromDatabase(doc, C_toolsCommandIds.Ddd.DimLinear, out _);
        DddLinearDimensionService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimLinearContinueInternal, CommandFlags.Modal | CommandFlags.NoHistory)]
    public void F_DcLinearDimensionContinueInternal()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        if (!DddLinearDimensionService.ContinueAfterNativeLinearDimension(doc))
            F_DcLinearDimension();
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimTextAvoid, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_DfAvoidDimensionText()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddDimensionTextAvoidanceService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimMerge, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_DvMergeDimensionRange()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddDimensionMergeService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimOuter, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_DqqCreateOuterDimension()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddDimensionOuterDimensionService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimFootEdit, CommandFlags.Modal)]
    public void F_DdeEditDimensionFoot()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddDimensionFootAdjustService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.TextEditorFix, CommandFlags.Modal)]
    public void F_TextEditFix()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddTextEditorFixService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.TextHistoryEdit, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_EdTextHistoryEdit()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        DddTextEditHostWorkflow.ShowWindowDialog(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.TextToMText, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_TtmTextToMText()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddTextToMTextService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.TextToLeader, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_DdcTextToLeader()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddTextToLeaderService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.TextMatch, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_AtMatchTextContent()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddTextMatchService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimShift, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_DsShiftDimensionPosition()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddDimensionShiftService.Run(doc);
    }

    [CommandMethod(DddPluginCommandIds.CommandGroup, DddPluginCommandIds.DimShiftHorizontal, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void F_DzShiftDimensionPosition()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        DddDimensionShiftService.RunHorizontal(doc);
    }

    internal static void CloseDddPanelIfAny()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        DddPanelHostWorkflow.ClosePanelIfAny(doc);
        DddTextEditHostWorkflow.CloseWindowIfAny(doc);
    }
}
