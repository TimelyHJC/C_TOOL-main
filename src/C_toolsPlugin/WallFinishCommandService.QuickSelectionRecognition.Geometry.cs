using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static partial class QuickSelectionRecognitionBuilder
    {
        private static bool TryReadQuickSelectionGeometry(
            Database db,
            IReadOnlyList<ObjectId> entityIds,
            out QuickSelectionGeometry? quickGeometry,
            out string error)
        {
            quickGeometry = null;
            error = "";

            using var tr = db.TransactionManager.StartTransaction();
            var openSegments = new List<QuickSegment>();
            var closedLoops = new List<QuickClosedLoop>();
            double? baseElevation = null;

            for (var i = 0; i < entityIds.Count; i++)
            {
                var entityId = entityIds[i];
                if (tr.GetObject(entityId, OpenMode.ForRead, openErased: false) is not Entity entity)
                    continue;

                switch (entity)
                {
                    case Line line:
                        if (line.Length <= PointTolerance ||
                            !GeometryHelpers.IsWorldXyPlane(line.Normal) ||
                            Math.Abs(line.StartPoint.Z - line.EndPoint.Z) > PointTolerance)
                        {
                            continue;
                        }

                        if (!TryUseQuickElevation(ref baseElevation, line.StartPoint.Z))
                            continue;

                        openSegments.Add(new QuickSegment(
                            entityId,
                            line.StartPoint.Z,
                            new Point2d(line.StartPoint.X, line.StartPoint.Y),
                            new Point2d(line.EndPoint.X, line.EndPoint.Y)));
                        break;

                    case Polyline polyline:
                        if (polyline.NumberOfVertices < 2 ||
                            polyline.Length <= PointTolerance ||
                            !GeometryHelpers.IsWorldXyPlane(polyline.Normal) ||
                            GeometryHelpers.HasArcSegments(polyline))
                        {
                            continue;
                        }

                        if (!TryUseQuickElevation(ref baseElevation, polyline.Elevation))
                            continue;

                        if (polyline.Closed)
                        {
                            var loopVertices = new List<Point2d>(polyline.NumberOfVertices);
                            for (var vertexIndex = 0; vertexIndex < polyline.NumberOfVertices; vertexIndex++)
                                loopVertices.Add(polyline.GetPoint2dAt(vertexIndex));

                            if (loopVertices.Count >= 3)
                                closedLoops.Add(new QuickClosedLoop(entityId, polyline.Elevation, loopVertices));

                            continue;
                        }

                        for (var vertexIndex = 0; vertexIndex < polyline.NumberOfVertices - 1; vertexIndex++)
                        {
                            var startPoint = polyline.GetPoint2dAt(vertexIndex);
                            var endPoint = polyline.GetPoint2dAt(vertexIndex + 1);
                            if (GeometryHelpers.ArePointsEqual(startPoint, endPoint))
                                continue;

                            openSegments.Add(new QuickSegment(entityId, polyline.Elevation, startPoint, endPoint));
                        }
                    break;
                }
            }

            SplitInteriorClosedLoopsFromOpenSegments(openSegments, closedLoops);

            if (openSegments.Count == 0)
            {
                error = "所选户型中未找到可识别的墙体线。";
                return false;
            }

            quickGeometry = new QuickSelectionGeometry(
                baseElevation ?? openSegments[0].Elevation,
                openSegments,
                closedLoops);
            return true;
        }

        private static bool TryUseQuickElevation(ref double? baseElevation, double elevation)
        {
            if (!baseElevation.HasValue)
            {
                baseElevation = elevation;
                return true;
            }

            return Math.Abs(baseElevation.Value - elevation) <= PointTolerance;
        }

        private static void SplitInteriorClosedLoopsFromOpenSegments(
            List<QuickSegment> openSegments,
            List<QuickClosedLoop> closedLoops)
        {
            if (openSegments.Count < 4)
                return;

            if (!TryGetQuickSegmentBounds(
                    openSegments,
                    out var overallMinX,
                    out var overallMinY,
                    out var overallMaxX,
                    out var overallMaxY))
            {
                return;
            }

            var nodePoints = new List<Point2d>();
            var nodeDegrees = new List<int>();
            var nodeLinks = new List<List<int>>();
            var linkNodeIndexes = new List<(int StartNodeIndex, int EndNodeIndex)>(openSegments.Count);
            for (var i = 0; i < openSegments.Count; i++)
            {
                var startNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, openSegments[i].StartPoint);
                var endNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, openSegments[i].EndPoint);
                linkNodeIndexes.Add((startNodeIndex, endNodeIndex));
                nodeDegrees[startNodeIndex]++;
                nodeDegrees[endNodeIndex]++;
                nodeLinks[startNodeIndex].Add(i);
                nodeLinks[endNodeIndex].Add(i);
            }

            var removed = new bool[openSegments.Count];
            var visited = new bool[openSegments.Count];
            var overallDiagonal = Math.Sqrt(
                Math.Pow(overallMaxX - overallMinX, 2.0) +
                Math.Pow(overallMaxY - overallMinY, 2.0));

            for (var seedIndex = 0; seedIndex < openSegments.Count; seedIndex++)
            {
                if (visited[seedIndex])
                    continue;

                var componentLinkIndexes = new List<int>();
                var componentNodeIndexes = new HashSet<int>();
                var stack = new Stack<int>();
                stack.Push(seedIndex);
                while (stack.Count > 0)
                {
                    var linkIndex = stack.Pop();
                    if (visited[linkIndex])
                        continue;

                    visited[linkIndex] = true;
                    componentLinkIndexes.Add(linkIndex);
                    var (startNodeIndex, endNodeIndex) = linkNodeIndexes[linkIndex];
                    componentNodeIndexes.Add(startNodeIndex);
                    componentNodeIndexes.Add(endNodeIndex);

                    var startAdjacentLinks = nodeLinks[startNodeIndex];
                    for (var adjacentIndex = 0; adjacentIndex < startAdjacentLinks.Count; adjacentIndex++)
                    {
                        var adjacentLinkIndex = startAdjacentLinks[adjacentIndex];
                        if (!visited[adjacentLinkIndex])
                            stack.Push(adjacentLinkIndex);
                    }

                    var endAdjacentLinks = nodeLinks[endNodeIndex];
                    for (var adjacentIndex = 0; adjacentIndex < endAdjacentLinks.Count; adjacentIndex++)
                    {
                        var adjacentLinkIndex = endAdjacentLinks[adjacentIndex];
                        if (!visited[adjacentLinkIndex])
                            stack.Push(adjacentLinkIndex);
                    }
                }

                if (!IsClosedLoopComponent(componentNodeIndexes, nodeDegrees))
                    continue;

                if (!TryOrderClosedLoopVertices(
                        openSegments,
                        linkNodeIndexes,
                        nodeLinks,
                        componentLinkIndexes,
                        out var orderedVertices))
                {
                    continue;
                }

                var normalizedVertices = NormalizeLoopVertices(orderedVertices);
                if (!TryGetVerticesBounds(
                        normalizedVertices,
                        out var componentMinX,
                        out var componentMinY,
                        out var componentMaxX,
                        out var componentMaxY))
                {
                    continue;
                }

                if (!IsInteriorColumnCandidate(
                        componentMinX,
                        componentMinY,
                        componentMaxX,
                        componentMaxY,
                        overallMinX,
                        overallMinY,
                        overallMaxX,
                        overallMaxY,
                        overallDiagonal))
                {
                    continue;
                }

                closedLoops.Add(new QuickClosedLoop(
                    openSegments[componentLinkIndexes[0]].SourceEntityId,
                    openSegments[componentLinkIndexes[0]].Elevation,
                    normalizedVertices));
                for (var i = 0; i < componentLinkIndexes.Count; i++)
                    removed[componentLinkIndexes[i]] = true;
            }

            var filteredOpenSegments = new List<QuickSegment>(openSegments.Count);
            var hasRemovedSegments = false;
            for (var i = 0; i < openSegments.Count; i++)
            {
                if (removed[i])
                {
                    hasRemovedSegments = true;
                    continue;
                }

                filteredOpenSegments.Add(openSegments[i]);
            }

            if (!hasRemovedSegments)
                return;

            openSegments.Clear();
            openSegments.AddRange(filteredOpenSegments);
        }

        private static bool TryGetQuickSegmentBounds(
            IReadOnlyList<QuickSegment> segments,
            out double minX,
            out double minY,
            out double maxX,
            out double maxY)
        {
            minX = double.MaxValue;
            minY = double.MaxValue;
            maxX = double.MinValue;
            maxY = double.MinValue;
            var found = false;

            for (var i = 0; i < segments.Count; i++)
            {
                var startPoint = segments[i].StartPoint;
                if (!found)
                {
                    minX = maxX = startPoint.X;
                    minY = maxY = startPoint.Y;
                    found = true;
                }
                else
                {
                    if (startPoint.X < minX) minX = startPoint.X;
                    if (startPoint.Y < minY) minY = startPoint.Y;
                    if (startPoint.X > maxX) maxX = startPoint.X;
                    if (startPoint.Y > maxY) maxY = startPoint.Y;
                }

                var endPoint = segments[i].EndPoint;
                if (!found)
                {
                    minX = maxX = endPoint.X;
                    minY = maxY = endPoint.Y;
                    found = true;
                    continue;
                }

                if (endPoint.X < minX) minX = endPoint.X;
                if (endPoint.Y < minY) minY = endPoint.Y;
                if (endPoint.X > maxX) maxX = endPoint.X;
                if (endPoint.Y > maxY) maxY = endPoint.Y;
            }

            return found;
        }

        private static bool IsClosedLoopComponent(
            IReadOnlyCollection<int> componentNodeIndexes,
            IReadOnlyList<int> nodeDegrees)
        {
            if (componentNodeIndexes.Count < 4)
                return false;

            foreach (var nodeIndex in componentNodeIndexes)
            {
                if (nodeDegrees[nodeIndex] != 2)
                    return false;
            }

            return true;
        }

        private static bool TryOrderClosedLoopVertices(
            IReadOnlyList<QuickSegment> segments,
            IReadOnlyList<(int StartNodeIndex, int EndNodeIndex)> linkNodeIndexes,
            IReadOnlyList<List<int>> nodeLinks,
            IReadOnlyList<int> componentLinkIndexes,
            out List<Point2d> orderedVertices)
        {
            orderedVertices = new List<Point2d>();
            if (componentLinkIndexes.Count == 0)
                return false;

            var componentLinkIndexSet = new HashSet<int>(componentLinkIndexes);
            var firstLinkIndex = componentLinkIndexes[0];
            var firstLink = segments[firstLinkIndex];
            var firstNodes = linkNodeIndexes[firstLinkIndex];
            var currentNodeIndex = firstNodes.EndNodeIndex;
            var startNodeIndex = firstNodes.StartNodeIndex;
            var visitedLinkIndexes = new HashSet<int> { firstLinkIndex };
            GeometryHelpers.AddVertexIfDistinct(orderedVertices, firstLink.StartPoint);
            GeometryHelpers.AddVertexIfDistinct(orderedVertices, firstLink.EndPoint);

            while (visitedLinkIndexes.Count < componentLinkIndexes.Count)
            {
                var nextLinkIndex = -1;
                var adjacentLinks = nodeLinks[currentNodeIndex];
                for (var i = 0; i < adjacentLinks.Count; i++)
                {
                    var candidateLinkIndex = adjacentLinks[i];
                    if (!componentLinkIndexSet.Contains(candidateLinkIndex) || visitedLinkIndexes.Contains(candidateLinkIndex))
                        continue;

                    nextLinkIndex = candidateLinkIndex;
                    break;
                }

                if (nextLinkIndex < 0)
                    return false;

                visitedLinkIndexes.Add(nextLinkIndex);
                var nextLink = segments[nextLinkIndex];
                var nextNodes = linkNodeIndexes[nextLinkIndex];
                var useForwardOrder = nextNodes.StartNodeIndex == currentNodeIndex;
                GeometryHelpers.AddVertexIfDistinct(
                    orderedVertices,
                    useForwardOrder ? nextLink.StartPoint : nextLink.EndPoint);
                GeometryHelpers.AddVertexIfDistinct(
                    orderedVertices,
                    useForwardOrder ? nextLink.EndPoint : nextLink.StartPoint);
                currentNodeIndex = useForwardOrder
                    ? nextNodes.EndNodeIndex
                    : nextNodes.StartNodeIndex;
            }

            return currentNodeIndex == startNodeIndex && orderedVertices.Count >= 4;
        }

        private static List<Point2d> NormalizeLoopVertices(IReadOnlyList<Point2d> rawVertices)
        {
            var vertices = new List<Point2d>(rawVertices.Count);
            for (var i = 0; i < rawVertices.Count; i++)
                GeometryHelpers.AddVertexIfDistinct(vertices, rawVertices[i]);

            if (vertices.Count > 1 && GeometryHelpers.ArePointsEqual(vertices[0], vertices[^1]))
                vertices.RemoveAt(vertices.Count - 1);

            return vertices;
        }

        private static bool TryGetVerticesBounds(
            IReadOnlyList<Point2d> vertices,
            out double minX,
            out double minY,
            out double maxX,
            out double maxY)
        {
            minX = double.MaxValue;
            minY = double.MaxValue;
            maxX = double.MinValue;
            maxY = double.MinValue;
            var found = false;

            for (var i = 0; i < vertices.Count; i++)
            {
                var point = vertices[i];
                if (!found)
                {
                    minX = maxX = point.X;
                    minY = maxY = point.Y;
                    found = true;
                    continue;
                }

                if (point.X < minX) minX = point.X;
                if (point.Y < minY) minY = point.Y;
                if (point.X > maxX) maxX = point.X;
                if (point.Y > maxY) maxY = point.Y;
            }

            return found;
        }

        private static bool IsInteriorColumnCandidate(
            double componentMinX,
            double componentMinY,
            double componentMaxX,
            double componentMaxY,
            double overallMinX,
            double overallMinY,
            double overallMaxX,
            double overallMaxY,
            double overallDiagonal)
        {
            var componentWidth = componentMaxX - componentMinX;
            var componentHeight = componentMaxY - componentMinY;
            if (componentWidth <= PointTolerance || componentHeight <= PointTolerance)
                return false;

            var maxColumnSize = Math.Max(Math.Min(overallDiagonal * 0.15, 3000.0), 900.0);
            if (componentWidth > maxColumnSize || componentHeight > maxColumnSize)
                return false;

            var boundaryMargin = Math.Max(overallDiagonal * 0.02, 150.0);
            if (componentMinX <= overallMinX + boundaryMargin ||
                componentMinY <= overallMinY + boundaryMargin ||
                componentMaxX >= overallMaxX - boundaryMargin ||
                componentMaxY >= overallMaxY - boundaryMargin)
            {
                return false;
            }

            return true;
        }
    }
}
