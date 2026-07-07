using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static partial class QuickSelectionRecognitionBuilder
    {
        private static bool TryRecognizeQuickWallSections(
            QuickSelectionGeometry quickGeometry,
            out List<QuickWallGuideCandidate> wallSections,
            out double estimatedWallThickness,
            out string error)
        {
            wallSections = new List<QuickWallGuideCandidate>();
            error = "";

            var matchedSections = new List<QuickWallGuideCandidate>();
            var wallThicknesses = new List<double>();
            for (var segmentIndex = 0; segmentIndex < quickGeometry.OpenSegments.Count; segmentIndex++)
            {
                var segment = quickGeometry.OpenSegments[segmentIndex];
                if (!TryFindBestQuickWallCounterpart(
                        quickGeometry.OpenSegments,
                        segmentIndex,
                        out var counterpart,
                        out var wallThickness) || counterpart == null)
                {
                    continue;
                }

                wallThicknesses.Add(wallThickness);
                if (!IsQuickInnerWallSegment(segment, counterpart, quickGeometry.CenterPoint))
                    continue;

                matchedSections.Add(new QuickWallGuideCandidate(segment, counterpart, wallThickness));
            }

            if (matchedSections.Count == 0)
            {
                estimatedWallThickness = 120.0;
                error = "未识别到户型外墙内线，请检查所选对象。";
                return false;
            }

            estimatedWallThickness = GeometryHelpers.ComputeRepresentativeDistance(wallThicknesses, 120.0);
            wallSections = FilterQuickBoundaryWallSections(matchedSections, quickGeometry.CenterPoint, estimatedWallThickness);
            if (wallSections.Count == 0)
                wallSections = DeduplicateQuickWallSections(matchedSections);

            return wallSections.Count > 0;
        }

        private static bool TryFindBestQuickWallCounterpart(
            IReadOnlyList<QuickSegment> segments,
            int segmentIndex,
            out QuickSegment? counterpart,
            out double wallThickness)
        {
            counterpart = null;
            wallThickness = 0.0;
            var source = segments[segmentIndex];
            var bestScore = double.MaxValue;

            for (var otherIndex = 0; otherIndex < segments.Count; otherIndex++)
            {
                if (otherIndex == segmentIndex)
                    continue;

                var other = segments[otherIndex];
                if (source.SourceEntityId == other.SourceEntityId)
                    continue;

                if (!TryEvaluateQuickWallPair(source, other, out var pairDistance, out var overlapLength))
                    continue;

                var score = pairDistance + Math.Abs(source.Length - overlapLength);
                if (score >= bestScore)
                    continue;

                bestScore = score;
                wallThickness = pairDistance;
                counterpart = other;
            }

            return counterpart != null;
        }

        private static bool TryEvaluateQuickWallPair(
            QuickSegment source,
            QuickSegment other,
            out double pairDistance,
            out double overlapLength)
        {
            pairDistance = 0.0;
            overlapLength = 0.0;

            if (!AreQuickSegmentsParallel(source, other))
                return false;

            var axis = source.CanonicalTangent;
            var normal = new Vector2d(-axis.Y, axis.X);
            var startDistance = GeometryHelpers.DotProduct(source.StartPoint, other.StartPoint, normal);
            var endDistance = GeometryHelpers.DotProduct(source.StartPoint, other.EndPoint, normal);
            if (Math.Abs(startDistance - endDistance) > QuickWallDistanceVariationTolerance)
                return false;

            var averageDistance = Math.Abs((startDistance + endDistance) * 0.5);
            if (averageDistance < QuickWallMinThickness || averageDistance > QuickWallMaxThickness)
                return false;

            GeometryHelpers.GetQuickSegmentProjectionRange(source, axis, out var sourceMin, out var sourceMax);
            GeometryHelpers.GetQuickSegmentProjectionRange(other, axis, out var otherMin, out var otherMax);
            overlapLength = Math.Min(sourceMax, otherMax) - Math.Max(sourceMin, otherMin);
            var minLength = Math.Min(source.Length, other.Length);
            if (overlapLength < Math.Max(minLength * QuickWallMinOverlapRatio, QuickWallMinThickness))
                return false;

            pairDistance = averageDistance;
            return true;
        }

        private static bool AreQuickSegmentsParallel(QuickSegment source, QuickSegment other)
        {
            var cross = Math.Abs(source.CanonicalTangent.X * other.CanonicalTangent.Y -
                                 source.CanonicalTangent.Y * other.CanonicalTangent.X);
            return cross <= QuickWallParallelTolerance;
        }

        private static bool IsQuickInnerWallSegment(
            QuickSegment source,
            QuickSegment counterpart,
            Point2d centerPoint)
        {
            var axis = source.CanonicalTangent;
            var normal = new Vector2d(-axis.Y, axis.X);
            var counterpartSide = GeometryHelpers.DotProduct(source.MidPoint, counterpart.MidPoint, normal);
            if (Math.Abs(counterpartSide) <= PointTolerance)
                return source.MidPoint.GetDistanceTo(centerPoint) <= counterpart.MidPoint.GetDistanceTo(centerPoint);

            if (counterpartSide < 0)
                normal = -normal;

            var sourceDistance = Math.Abs(GeometryHelpers.DotProduct(source.MidPoint, centerPoint, normal));
            var counterpartDistance = Math.Abs(GeometryHelpers.DotProduct(counterpart.MidPoint, centerPoint, normal));
            if (Math.Abs(sourceDistance - counterpartDistance) <= PointTolerance)
                return source.MidPoint.GetDistanceTo(centerPoint) <= counterpart.MidPoint.GetDistanceTo(centerPoint);

            return sourceDistance < counterpartDistance;
        }

        private static List<QuickWallGuideCandidate> DeduplicateQuickWallSections(
            IReadOnlyList<QuickWallGuideCandidate> wallSections)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var distinct = new List<QuickWallGuideCandidate>(wallSections.Count);
            for (var i = 0; i < wallSections.Count; i++)
            {
                var key = GeometryHelpers.GetQuickSegmentKey(wallSections[i].InnerSegment);
                if (!seen.Add(key))
                    continue;

                distinct.Add(wallSections[i]);
            }

            return distinct;
        }
    }
}
