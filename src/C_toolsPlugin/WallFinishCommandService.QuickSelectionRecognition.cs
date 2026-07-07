using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static partial class QuickSelectionRecognitionBuilder
    {
        internal static bool TryCreateQuickSelection(
            Document doc,
            IReadOnlyList<ObjectId> entityIds,
            out SourceCurveSelection? selection,
            out string error,
            out bool canceled)
        {
            selection = null;
            error = "";
            canceled = false;

            if (entityIds.Count == 0)
            {
                error = "未选择户型对象。";
                return false;
            }

            if (!TryReadQuickSelectionGeometry(doc.Database, entityIds, out var quickGeometry, out error) ||
                quickGeometry == null)
            {
                return false;
            }

            if (!TryCreateQuickWallGuideSelections(
                    quickGeometry,
                    out var wallGuides,
                    out var wallSections,
                    out var estimatedWallThickness,
                    out error))
            {
                return false;
            }

            var guideSelections = new List<GuideChainSelection>(wallGuides.Count);
            guideSelections.AddRange(wallGuides);
            var transferred = false;

            try
            {
                var doorCandidates = QuickSelectionFeatureBuilder.BuildQuickDoorCandidates(wallSections, estimatedWallThickness);
                if (doorCandidates.Count > 0)
                {
                    if (!QuickSelectionFeatureBuilder.TryPromptStorefrontDoorNumbers(
                            doc,
                            doorCandidates,
                            estimatedWallThickness,
                            out var storefrontNumbers,
                            out error,
                            out canceled))
                    {
                        return false;
                    }

                    QuickSelectionFeatureBuilder.AddQuickStorefrontGuides(guideSelections, doorCandidates, storefrontNumbers);
                }

                QuickSelectionFeatureBuilder.AddQuickColumnGuides(guideSelections, quickGeometry, estimatedWallThickness);
                if (guideSelections.Count == 0)
                {
                    error = "未识别到可用的完成面导向线。";
                    return false;
                }

                selection = new SourceCurveSelection(
                    guideSelections,
                    useRecognizedFigureDirection: true,
                    previewBasePoint: quickGeometry.CenterPoint3d);
                transferred = true;
                return true;
            }
            finally
            {
                if (!transferred)
                    DisposeGuideSelections(guideSelections);
            }
        }

        private static bool TryCreateQuickWallGuideSelections(
            QuickSelectionGeometry quickGeometry,
            out List<GuideChainSelection> guideSelections,
            out List<QuickWallGuideCandidate> wallSections,
            out double estimatedWallThickness,
            out string error)
        {
            guideSelections = new List<GuideChainSelection>();
            wallSections = new List<QuickWallGuideCandidate>();
            error = "";

            if (!TryRecognizeQuickWallSections(
                    quickGeometry,
                    out wallSections,
                    out estimatedWallThickness,
                    out error))
            {
                return false;
            }

            var wallParts = new List<SourceChainPart>(wallSections.Count);
            for (var i = 0; i < wallSections.Count; i++)
            {
                wallParts.Add(new SourceChainPart(
                    wallSections[i].InnerSegment.SourceEntityId,
                    wallSections[i].InnerSegment.Elevation,
                    new List<Point2d>
                    {
                        wallSections[i].InnerSegment.StartPoint,
                        wallSections[i].InnerSegment.EndPoint
                    }));
            }

            if (!QuickSelectionFeatureBuilder.TryBuildQuickWallGuideSelections(
                    wallParts,
                    quickGeometry.CenterPoint3d,
                    out guideSelections,
                    out error))
            {
                DisposeGuideSelections(guideSelections);
                guideSelections = QuickSelectionFeatureBuilder.CreateFallbackQuickWallGuides(wallSections, quickGeometry.CenterPoint3d);
                if (guideSelections.Count > 0)
                {
                    error = "";
                    return true;
                }

                return false;
            }

            return guideSelections.Count > 0;
        }
    }
}
