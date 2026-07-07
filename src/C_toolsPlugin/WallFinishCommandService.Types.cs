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

    internal enum SourcePromptStatus
    {
        Success,
        EndCommand,
        Cancel
    }

    internal enum ModePromptStatus
    {
        Success,
        Cancel
    }

    internal enum WallFinishMode
    {
        Quick,
        Standard
    }

    internal sealed class SourceCurveSelection : IDisposable
    {
        internal SourceCurveSelection(IReadOnlyList<GuideChainSelection> guideSelections)
            : this(
                guideSelections,
                StandardSelectionBuilder.CanRecognizeFigureInterior(GetDirectionReferenceGuides(guideSelections)),
                StandardSelectionBuilder.ComputeRecognizedFigureCenter(GetDirectionReferenceGuides(guideSelections)))
        {
        }

        internal SourceCurveSelection(
            IReadOnlyList<GuideChainSelection> guideSelections,
            bool useRecognizedFigureDirection,
            Point3d previewBasePoint)
        {
            GuideSelections = guideSelections;
            UseRecognizedFigureDirection = useRecognizedFigureDirection;
            PreviewBasePoint = previewBasePoint;
        }

        internal IReadOnlyList<GuideChainSelection> GuideSelections { get; }

        internal bool UseRecognizedFigureDirection { get; }

        internal Point3d PreviewBasePoint { get; }

        public void Dispose()
        {
            for (var i = 0; i < GuideSelections.Count; i++)
                GuideSelections[i].Dispose();
        }

        private static IReadOnlyList<GuideChainSelection> GetDirectionReferenceGuides(
            IReadOnlyList<GuideChainSelection> guideSelections)
        {
            var references = guideSelections.Where(x => x.UseForDirectionResolution).ToList();
            return references.Count > 0 ? references : guideSelections;
        }
    }

    internal sealed class GuideChainSelection : IDisposable
    {
        internal GuideChainSelection(
            ObjectId propertySourceEntityId,
            Polyline guidePolyline,
            int recognizedInteriorSideSign = 1,
            bool useForDirectionResolution = true,
            IReadOnlyList<ObjectId>? hatchBoundaryEntityIds = null)
        {
            PropertySourceEntityId = propertySourceEntityId;
            GuidePolyline = guidePolyline;
            RecognizedInteriorSideSign = recognizedInteriorSideSign < 0 ? -1 : 1;
            UseForDirectionResolution = useForDirectionResolution;
            HatchBoundaryEntityIds = hatchBoundaryEntityIds?
                .Where(x => !x.IsNull)
                .Distinct()
                .ToArray() ?? Array.Empty<ObjectId>();
        }

        internal ObjectId PropertySourceEntityId { get; }

        internal Polyline GuidePolyline { get; }

        internal int RecognizedInteriorSideSign { get; }

        internal bool UseForDirectionResolution { get; }

        internal IReadOnlyList<ObjectId> HatchBoundaryEntityIds { get; }

        public void Dispose()
        {
            GuidePolyline.Dispose();
        }
    }

    internal sealed class SourceChainPart
    {
        internal SourceChainPart(
            ObjectId entityId,
            double elevation,
            IReadOnlyList<Point2d> vertices,
            bool isClosed = false)
        {
            EntityId = entityId;
            Elevation = elevation;
            Vertices = vertices;
            IsClosed = isClosed;
        }

        internal ObjectId EntityId { get; }

        internal double Elevation { get; }

        internal IReadOnlyList<Point2d> Vertices { get; }

        internal bool IsClosed { get; }

        internal Point2d StartPoint => Vertices[0];

        internal Point2d EndPoint => Vertices[^1];
    }

    private readonly struct SourceChainLink
    {
        internal SourceChainLink(SourceChainPart part, int startNodeIndex, int endNodeIndex)
        {
            Part = part;
            StartNodeIndex = startNodeIndex;
            EndNodeIndex = endNodeIndex;
        }

        internal SourceChainPart Part { get; }

        internal int StartNodeIndex { get; }

        internal int EndNodeIndex { get; }
    }

    internal readonly struct WallFinishResolvedSettings
    {
        internal WallFinishResolvedSettings(
            string targetLayerName,
            LayerShortcutEntry? targetLayerEntry,
            LayerShortcutEntry? hatchStyleEntry,
            int? targetLayerColorIndex)
        {
            TargetLayerName = string.IsNullOrWhiteSpace(targetLayerName) ? "0" : targetLayerName.Trim();
            TargetLayerEntry = targetLayerEntry;
            TargetLayerColorIndex = targetLayerColorIndex is >= 1 and <= 255 ? targetLayerColorIndex : null;
            PreviewColorIndex = TargetLayerColorIndex ?? targetLayerEntry?.ColorIndex;
            HatchLayerEntry = hatchStyleEntry;
            HatchLayerName = string.IsNullOrWhiteSpace(hatchStyleEntry?.LayerName)
                ? DefaultFinishHatchLayerName
                : hatchStyleEntry!.LayerName.Trim();
            HatchStyle = SettingsManager.ResolveHatchStyle(hatchStyleEntry ?? targetLayerEntry);
        }

        internal string TargetLayerName { get; }

        internal LayerShortcutEntry? TargetLayerEntry { get; }

        internal int? TargetLayerColorIndex { get; }

        internal int? PreviewColorIndex { get; }

        internal string HatchLayerName { get; }

        internal LayerShortcutEntry? HatchLayerEntry { get; }

        internal HatchStyleSnapshot HatchStyle { get; }
    }

    internal readonly struct WallFinishCreateResult
    {
        internal WallFinishCreateResult(int createdCount, string layerSummary)
        {
            CreatedCount = createdCount;
            LayerSummary = layerSummary;
        }

        internal int CreatedCount { get; }

        internal string LayerSummary { get; }
    }
}
