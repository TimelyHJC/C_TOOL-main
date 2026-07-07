using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Gi = Autodesk.AutoCAD.GraphicsInterface;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{

    private const string CommandName = PluginCommandIds.WallFinish;
    private const string SettingsKeyword = "S";
    private const string FinishLayerDescription = "完成面";
    private const string FinishHatchDescription = "完成面填充";
    private const string DefaultFinishHatchLayerName = "A-新隔墙";
    private const string SolidPatternName = "SOLID";
    private const string AutoLayerDisplayName = "（自动：完成面图层/源图层）";
    private const double PointTolerance = 1e-6;
    private const double RecognizedFigureWideCoverageRatio = 0.6;
    private const double RecognizedFigureTallCoverageRatio = 0.2;
    private const double QuickWallMinThickness = 20.0;
    private const double QuickWallMaxThickness = 1500.0;
    private const double QuickWallParallelTolerance = 0.015;
    private const double QuickWallDistanceVariationTolerance = 30.0;
    private const double QuickWallMinOverlapRatio = 0.35;
    private const int QuickBoundarySectorCount = 72;
    private const double QuickDoorMinWidth = 450.0;
    private const double QuickDoorMaxWidth = 3000.0;
    private const double QuickColumnMaxSizeMultiplier = 6.0;
    private const short QuickDoorLabelColorIndex = 2;
    private const short DefaultLayerColorIndex = 7;

    internal static void Run()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var settings = WallFinishSettingsStore.LoadOrDefault();

        try
        {
            while (true)
            {
                var sourceStatus = PromptSourceCurve(doc, ref settings, out var sourceSelection);
                if (sourceStatus == SourcePromptStatus.EndCommand)
                    return;

                if (sourceStatus == SourcePromptStatus.Cancel || sourceSelection == null)
                {
                    ed.WriteMessage("\nC_TOOL：已取消。");
                    return;
                }

                using (sourceSelection)
                {
                    if (!OutlineBuilder.PromptOffsetSideWithPreview(
                             doc,
                             sourceSelection,
                             ref settings,
                             out var directionSelection))
                    {
                        ed.WriteMessage("\nC_TOOL：已取消。");
                        return;
                    }

                    var offsetDistance = settings.OffsetDistance;
                    var createResult = OutlineBuilder.CreateWallFinishOutlines(
                        doc,
                        sourceSelection,
                        settings,
                        offsetDistance,
                        directionSelection);
                    ed.WriteMessage(
                        $"\nC_TOOL：已生成完成面，图层 {createResult.LayerSummary}，偏移量 {SettingsManager.FormatDistance(offsetDistance)}。");
                }
            }
        }
        catch (System.Exception ex) when (ex is not OperationCanceledException)
        {
            var errorType = ex switch
            {
                InvalidOperationException => "无效操作",
                ArgumentException => "参数错误",
                Autodesk.AutoCAD.Runtime.Exception => "CAD",
                _ => "未知"
            };
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败（{errorType}）", ex);
            ed.WriteMessage($"\nC_TOOL：失败：{ex.Message}");
        }
    }

    private static SourcePromptStatus PromptSourceCurve(
        Document doc,
        ref WallFinishSettingsDto settings,
        out SourceCurveSelection? selection)
    {
        selection = null;
        var ed = doc.Editor;

        while (true)
        {
            var promptStatus = TryGetSelectionIds(doc, ref settings, out var entityIds);
            if (promptStatus != SourcePromptStatus.Success)
                return promptStatus;

            if (StandardSelectionBuilder.TryCreateSelection(doc.Database, entityIds, out selection, out var standardError))
                return SourcePromptStatus.Success;

            ed.WriteMessage("\nC_TOOL：" + standardError);
        }
    }

    private static SourcePromptStatus TryGetSelectionIds(
        Document doc,
        ref WallFinishSettingsDto settings,
        out ObjectId[] entityIds)
    {
        var ed = doc.Editor;
        entityIds = Array.Empty<ObjectId>();

        try
        {
            var implied = ed.SelectImplied();
            if (implied.Status != PromptStatus.OK || implied.Value == null)
                return StandardSelectionBuilder.PromptSelectionIds(doc, ref settings, out entityIds);

            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            entityIds = StandardSelectionBuilder.NormalizeSelectionIds(implied.Value.GetObjectIds());
            return entityIds.Length > 0
                ? SourcePromptStatus.Success
                : StandardSelectionBuilder.PromptSelectionIds(doc, ref settings, out entityIds);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 读取预选对象失败（无效操作）", ex);
            return StandardSelectionBuilder.PromptSelectionIds(doc, ref settings, out entityIds);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 读取预选对象失败（CAD）", ex);
            return StandardSelectionBuilder.PromptSelectionIds(doc, ref settings, out entityIds);
        }
    }


}
