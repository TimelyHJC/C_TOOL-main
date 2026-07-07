namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static partial class QuickSelectionFeatureBuilder
    {
        internal static void AddQuickColumnGuides(
            List<GuideChainSelection> guideSelections,
            QuickSelectionGeometry quickGeometry,
            double estimatedWallThickness)
        {
            if (quickGeometry.ClosedLoops.Count == 0)
                return;

            var maxColumnSize = Math.Min(
                Math.Max(estimatedWallThickness * QuickColumnMaxSizeMultiplier, 900.0),
                Math.Max(quickGeometry.BoundsDiagonal * 0.35, 900.0));

            for (var i = 0; i < quickGeometry.ClosedLoops.Count; i++)
            {
                var loop = quickGeometry.ClosedLoops[i];
                if (loop.BoundsWidth <= PointTolerance ||
                    loop.BoundsHeight <= PointTolerance ||
                    loop.BoundsWidth > maxColumnSize ||
                    loop.BoundsHeight > maxColumnSize)
                {
                    continue;
                }

                var guideSelection = StandardSelectionBuilder.CreateClosedLoopGuideSelection(loop.SourceEntityId, loop.Vertices, loop.Elevation);
                if (guideSelection != null)
                    guideSelections.Add(guideSelection);
            }
        }
    }
}
