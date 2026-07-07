using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static partial class QuickSelectionRecognitionBuilder
    {
        private static List<QuickWallGuideCandidate> FilterQuickBoundaryWallSections(
            IReadOnlyList<QuickWallGuideCandidate> wallSections,
            Point2d centerPoint,
            double estimatedWallThickness)
        {
            var distinctSections = DeduplicateQuickWallSections(wallSections);
            if (distinctSections.Count <= 4)
                return distinctSections;

            var sectorMaxRadii = BuildQuickBoundarySectorMaxRadii(distinctSections, centerPoint);
            var keepDistance = Math.Max(estimatedWallThickness * 2.0, 120.0);

            var exteriorFiltered = FilterQuickBoundarySectionsByExteriorLeaves(
                distinctSections,
                centerPoint,
                sectorMaxRadii,
                keepDistance);
            if (exteriorFiltered.Count > 0)
            {
                var dominantExteriorComponent = SelectDominantQuickBoundaryComponent(exteriorFiltered);
                if (dominantExteriorComponent.Count > 0)
                    return dominantExteriorComponent;
            }

            var radialFiltered = FilterQuickBoundarySectionsByRadius(
                distinctSections,
                centerPoint,
                sectorMaxRadii,
                keepDistance);
            if (radialFiltered.Count == 0)
                return distinctSections;

            var dominantRadialComponent = SelectDominantQuickBoundaryComponent(radialFiltered);
            return dominantRadialComponent.Count > 0 ? dominantRadialComponent : radialFiltered;
        }

        private static double[] BuildQuickBoundarySectorMaxRadii(
            IReadOnlyList<QuickWallGuideCandidate> wallSections,
            Point2d centerPoint)
        {
            var sectorMaxRadii = new double[QuickBoundarySectorCount];
            for (var i = 0; i < sectorMaxRadii.Length; i++)
                sectorMaxRadii[i] = double.MinValue;

            for (var i = 0; i < wallSections.Count; i++)
            {
                var radius = wallSections[i].InnerSegment.MidPoint.GetDistanceTo(centerPoint);
                var sectorIndex = GetQuickBoundarySectorIndex(centerPoint, wallSections[i].InnerSegment.MidPoint);
                if (radius > sectorMaxRadii[sectorIndex])
                    sectorMaxRadii[sectorIndex] = radius;
            }

            return sectorMaxRadii;
        }

        private static List<QuickWallGuideCandidate> FilterQuickBoundarySectionsByExteriorLeaves(
            IReadOnlyList<QuickWallGuideCandidate> wallSections,
            Point2d centerPoint,
            IReadOnlyList<double> sectorMaxRadii,
            double keepDistance)
        {
            var nodePoints = new List<Point2d>();
            var nodeDegrees = new List<int>();
            var nodeLinks = new List<List<int>>();
            var linkNodeIndexes = new List<(int StartNodeIndex, int EndNodeIndex)>(wallSections.Count);

            for (var i = 0; i < wallSections.Count; i++)
            {
                var startNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, wallSections[i].InnerSegment.StartPoint);
                var endNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, wallSections[i].InnerSegment.EndPoint);
                linkNodeIndexes.Add((startNodeIndex, endNodeIndex));
                nodeDegrees[startNodeIndex]++;
                nodeDegrees[endNodeIndex]++;
                nodeLinks[startNodeIndex].Add(i);
                nodeLinks[endNodeIndex].Add(i);
            }

            var removedLinks = new bool[wallSections.Count];
            var queue = new Queue<int>();
            for (var i = 0; i < nodeDegrees.Count; i++)
            {
                if (nodeDegrees[i] <= 1)
                    queue.Enqueue(i);
            }

            while (queue.Count > 0)
            {
                var nodeIndex = queue.Dequeue();
                if (nodeDegrees[nodeIndex] != 1)
                    continue;

                var liveLinkIndex = -1;
                var adjacentLinks = nodeLinks[nodeIndex];
                for (var i = 0; i < adjacentLinks.Count; i++)
                {
                    var candidateLinkIndex = adjacentLinks[i];
                    if (!removedLinks[candidateLinkIndex])
                    {
                        liveLinkIndex = candidateLinkIndex;
                        break;
                    }
                }

                if (liveLinkIndex < 0)
                    continue;

                var section = wallSections[liveLinkIndex];
                var radius = section.InnerSegment.MidPoint.GetDistanceTo(centerPoint);
                var localMaxRadius = GetQuickBoundaryLocalMaxRadius(
                    centerPoint,
                    section.InnerSegment.MidPoint,
                    sectorMaxRadii);
                if (localMaxRadius != double.MinValue && radius >= localMaxRadius - keepDistance)
                    continue;

                RemoveLink(liveLinkIndex);
            }

            var filtered = new List<QuickWallGuideCandidate>(wallSections.Count);
            for (var i = 0; i < wallSections.Count; i++)
            {
                if (!removedLinks[i])
                    filtered.Add(wallSections[i]);
            }

            return filtered;

            void RemoveLink(int linkIndex)
            {
                if (removedLinks[linkIndex])
                    return;

                removedLinks[linkIndex] = true;
                var (startNodeIndex, endNodeIndex) = linkNodeIndexes[linkIndex];
                nodeDegrees[startNodeIndex]--;
                nodeDegrees[endNodeIndex]--;
                if (nodeDegrees[startNodeIndex] == 1)
                    queue.Enqueue(startNodeIndex);
                if (nodeDegrees[endNodeIndex] == 1)
                    queue.Enqueue(endNodeIndex);
            }
        }

        private static double GetQuickBoundaryLocalMaxRadius(
            Point2d centerPoint,
            Point2d point,
            IReadOnlyList<double> sectorMaxRadii)
        {
            var sectorIndex = GetQuickBoundarySectorIndex(centerPoint, point);
            var localMaxRadius = double.MinValue;
            for (var offset = -2; offset <= 2; offset++)
            {
                var lookupIndex = (sectorIndex + offset + QuickBoundarySectorCount) % QuickBoundarySectorCount;
                if (sectorMaxRadii[lookupIndex] > localMaxRadius)
                    localMaxRadius = sectorMaxRadii[lookupIndex];
            }

            return localMaxRadius;
        }

        private static List<QuickWallGuideCandidate> FilterQuickBoundarySectionsByRadius(
            IReadOnlyList<QuickWallGuideCandidate> wallSections,
            Point2d centerPoint,
            IReadOnlyList<double> sectorMaxRadii,
            double keepDistance)
        {
            var filtered = new List<QuickWallGuideCandidate>(wallSections.Count);
            for (var i = 0; i < wallSections.Count; i++)
            {
                var radius = wallSections[i].InnerSegment.MidPoint.GetDistanceTo(centerPoint);
                var localMaxRadius = GetQuickBoundaryLocalMaxRadius(
                    centerPoint,
                    wallSections[i].InnerSegment.MidPoint,
                    sectorMaxRadii);
                if (localMaxRadius == double.MinValue || radius >= localMaxRadius - keepDistance)
                    filtered.Add(wallSections[i]);
            }

            return filtered;
        }

        private static List<QuickWallGuideCandidate> SelectDominantQuickBoundaryComponent(
            IReadOnlyList<QuickWallGuideCandidate> wallSections)
        {
            if (wallSections.Count == 0)
                return new List<QuickWallGuideCandidate>();

            var nodePoints = new List<Point2d>();
            var nodeDegrees = new List<int>();
            var nodeLinks = new List<List<int>>();
            var linkNodeIndexes = new List<(int StartNodeIndex, int EndNodeIndex)>(wallSections.Count);
            for (var i = 0; i < wallSections.Count; i++)
            {
                var startNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, wallSections[i].InnerSegment.StartPoint);
                var endNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, wallSections[i].InnerSegment.EndPoint);
                linkNodeIndexes.Add((startNodeIndex, endNodeIndex));
                nodeLinks[startNodeIndex].Add(i);
                nodeLinks[endNodeIndex].Add(i);
            }

            var visitedLinks = new bool[wallSections.Count];
            var bestComponentIndexes = new List<int>();
            var bestArea = double.MinValue;
            var bestLength = double.MinValue;

            for (var i = 0; i < wallSections.Count; i++)
            {
                if (visitedLinks[i])
                    continue;

                var componentLinkIndexes = new List<int>();
                var componentNodeIndexes = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(i);

                while (stack.Count > 0)
                {
                    var linkIndex = stack.Pop();
                    if (visitedLinks[linkIndex])
                        continue;

                    visitedLinks[linkIndex] = true;
                    componentLinkIndexes.Add(linkIndex);
                    var (startNodeIndex, endNodeIndex) = linkNodeIndexes[linkIndex];
                    componentNodeIndexes.Add(startNodeIndex);
                    componentNodeIndexes.Add(endNodeIndex);

                    var startAdjacentLinks = nodeLinks[startNodeIndex];
                    for (var adjacentIndex = 0; adjacentIndex < startAdjacentLinks.Count; adjacentIndex++)
                    {
                        var adjacentLinkIndex = startAdjacentLinks[adjacentIndex];
                        if (!visitedLinks[adjacentLinkIndex])
                            stack.Push(adjacentLinkIndex);
                    }

                    var endAdjacentLinks = nodeLinks[endNodeIndex];
                    for (var adjacentIndex = 0; adjacentIndex < endAdjacentLinks.Count; adjacentIndex++)
                    {
                        var adjacentLinkIndex = endAdjacentLinks[adjacentIndex];
                        if (!visitedLinks[adjacentLinkIndex])
                            stack.Push(adjacentLinkIndex);
                    }
                }

                if (componentLinkIndexes.Count == 0)
                    continue;

                var minX = double.MaxValue;
                var minY = double.MaxValue;
                var maxX = double.MinValue;
                var maxY = double.MinValue;
                var totalLength = 0.0;
                foreach (var nodeIndex in componentNodeIndexes)
                {
                    var point = nodePoints[nodeIndex];
                    if (point.X < minX) minX = point.X;
                    if (point.Y < minY) minY = point.Y;
                    if (point.X > maxX) maxX = point.X;
                    if (point.Y > maxY) maxY = point.Y;
                }

                for (var linkOffset = 0; linkOffset < componentLinkIndexes.Count; linkOffset++)
                    totalLength += wallSections[componentLinkIndexes[linkOffset]].InnerSegment.Length;

                var area = (maxX - minX) * (maxY - minY);
                if (area > bestArea ||
                    (Math.Abs(area - bestArea) <= PointTolerance && totalLength > bestLength))
                {
                    bestArea = area;
                    bestLength = totalLength;
                    bestComponentIndexes = componentLinkIndexes;
                }
            }

            var bestComponent = new List<QuickWallGuideCandidate>(bestComponentIndexes.Count);
            for (var i = 0; i < bestComponentIndexes.Count; i++)
                bestComponent.Add(wallSections[bestComponentIndexes[i]]);
            return bestComponent;
        }

        private static int GetQuickBoundarySectorIndex(Point2d centerPoint, Point2d point)
        {
            var angle = Math.Atan2(point.Y - centerPoint.Y, point.X - centerPoint.X);
            if (angle < 0)
                angle += Math.PI * 2.0;

            var sector = (int)Math.Floor((angle / (Math.PI * 2.0)) * QuickBoundarySectorCount);
            if (sector < 0)
                return 0;

            if (sector >= QuickBoundarySectorCount)
                return QuickBoundarySectorCount - 1;

            return sector;
        }
    }
}
