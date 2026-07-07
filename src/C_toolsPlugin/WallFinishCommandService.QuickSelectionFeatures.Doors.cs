using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static partial class QuickSelectionFeatureBuilder
    {
        internal static List<QuickDoorCandidate> BuildQuickDoorCandidates(
            IReadOnlyList<QuickWallGuideCandidate> wallSections,
            double estimatedWallThickness)
        {
            var groups = new Dictionary<string, List<QuickWallGuideCandidate>>(StringComparer.Ordinal);
            for (var i = 0; i < wallSections.Count; i++)
            {
                var key = wallSections[i].FacadeKey(estimatedWallThickness);
                if (!groups.TryGetValue(key, out var items))
                {
                    items = new List<QuickWallGuideCandidate>();
                    groups.Add(key, items);
                }

                items.Add(wallSections[i]);
            }

            var doorCandidates = new List<QuickDoorCandidate>();
            var nextNumber = 1;
            foreach (var pair in groups)
            {
                var items = pair.Value;
                items.Sort((left, right) => left.InnerMinProjection.CompareTo(right.InnerMinProjection));
                for (var index = 0; index < items.Count - 1; index++)
                {
                    if (!TryCreateQuickDoorCandidate(
                            items[index],
                            items[index + 1],
                            estimatedWallThickness,
                            nextNumber,
                            out var candidate) || candidate == null)
                    {
                        continue;
                    }

                    doorCandidates.Add(candidate);
                    nextNumber++;
                }
            }

            return doorCandidates;
        }

        private static bool TryCreateQuickDoorCandidate(
            QuickWallGuideCandidate left,
            QuickWallGuideCandidate right,
            double estimatedWallThickness,
            int number,
            out QuickDoorCandidate? candidate)
        {
            candidate = null;

            var innerGap = right.InnerMinProjection - left.InnerMaxProjection;
            if (innerGap < Math.Max(estimatedWallThickness * 1.2, QuickDoorMinWidth) ||
                innerGap > Math.Max(estimatedWallThickness * 12.0, QuickDoorMaxWidth))
            {
                return false;
            }

            var outerGap = right.OuterMinProjection - left.OuterMaxProjection;
            if (outerGap < Math.Max(estimatedWallThickness * 1.2, QuickDoorMinWidth) ||
                outerGap > Math.Max(estimatedWallThickness * 12.0, QuickDoorMaxWidth))
            {
                return false;
            }

            if (Math.Abs(innerGap - outerGap) > Math.Max(estimatedWallThickness, Math.Min(innerGap, outerGap) * 0.35))
                return false;

            var leftInnerPoint = left.InnerEndPoint;
            var leftOuterPoint = left.OuterEndPoint;
            var rightInnerPoint = right.InnerStartPoint;
            var rightOuterPoint = right.OuterStartPoint;
            if (leftInnerPoint.GetDistanceTo(leftOuterPoint) <= PointTolerance ||
                rightInnerPoint.GetDistanceTo(rightOuterPoint) <= PointTolerance)
            {
                return false;
            }

            var openingCenter = GeometryHelpers.AveragePoint(
                GeometryHelpers.AveragePoint(leftInnerPoint, rightInnerPoint),
                GeometryHelpers.AveragePoint(leftOuterPoint, rightOuterPoint));
            var labelOffset = Math.Max(((left.WallThickness + right.WallThickness) * 0.5) * 1.6, 150.0);
            var labelPosition = openingCenter + (left.OutwardNormal * labelOffset);
            candidate = new QuickDoorCandidate(
                number,
                left.InnerSegment.Elevation,
                left.InnerSegment.SourceEntityId,
                right.InnerSegment.SourceEntityId,
                leftInnerPoint,
                leftOuterPoint,
                rightInnerPoint,
                rightOuterPoint,
                openingCenter,
                labelPosition);
            return true;
        }

        internal static void AddQuickStorefrontGuides(
            List<GuideChainSelection> guideSelections,
            IReadOnlyList<QuickDoorCandidate> doorCandidates,
            ISet<int> storefrontNumbers)
        {
            if (storefrontNumbers.Count == 0)
                return;

            for (var i = 0; i < doorCandidates.Count; i++)
            {
                var candidate = doorCandidates[i];
                if (!storefrontNumbers.Contains(candidate.Number))
                    continue;

                AddQuickDoorGuide(
                    guideSelections,
                    candidate.LeftSourceEntityId,
                    candidate.LeftInnerPoint,
                    candidate.LeftOuterPoint,
                    candidate.OpeningCenter3d,
                    candidate.Elevation);
                AddQuickDoorGuide(
                    guideSelections,
                    candidate.RightSourceEntityId,
                    candidate.RightInnerPoint,
                    candidate.RightOuterPoint,
                    candidate.OpeningCenter3d,
                    candidate.Elevation);
            }
        }

        private static void AddQuickDoorGuide(
            List<GuideChainSelection> guideSelections,
            ObjectId sourceEntityId,
            Point2d innerPoint,
            Point2d outerPoint,
            Point3d openingCenter,
            double elevation)
        {
            if (innerPoint.GetDistanceTo(outerPoint) <= PointTolerance)
                return;

            var guidePolyline = GeometryHelpers.CreateGuidePolyline(
                new[]
                {
                    innerPoint,
                    outerPoint
                },
                elevation);
            var sideSign = GeometryHelpers.ResolveSideSign(guidePolyline, openingCenter, 1);
            guideSelections.Add(new GuideChainSelection(
                sourceEntityId,
                guidePolyline,
                sideSign,
                useForDirectionResolution: false));
        }
    }
}
