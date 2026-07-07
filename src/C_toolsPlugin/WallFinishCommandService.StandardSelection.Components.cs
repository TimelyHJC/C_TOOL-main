using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static partial class StandardSelectionBuilder
    {
        private static bool TryBuildGuideSelectionsForComponent(
            IReadOnlyList<SourceChainLink> links,
            IReadOnlyList<int> nodeDegrees,
            IReadOnlyList<List<int>> nodeLinks,
            bool[] globalVisitedLinks,
            int seedLinkIndex,
            double elevation,
            out List<GuideChainSelection> guideSelections,
            out bool requiresRecognizedFigureSigns,
            out string error)
        {
            guideSelections = new List<GuideChainSelection>();
            requiresRecognizedFigureSigns = false;
            error = "";

            var componentLinkIndexes = new List<int>();
            var stack = new Stack<int>();
            stack.Push(seedLinkIndex);

            while (stack.Count > 0)
            {
                var linkIndex = stack.Pop();
                if (globalVisitedLinks[linkIndex])
                    continue;

                globalVisitedLinks[linkIndex] = true;
                componentLinkIndexes.Add(linkIndex);

                var link = links[linkIndex];
                AddAdjacentLinks(link.StartNodeIndex);
                AddAdjacentLinks(link.EndNodeIndex);
            }

            var endpointNodeIndexes = new List<int>(2);
            var componentNodeIndexes = new HashSet<int>();
            for (var i = 0; i < componentLinkIndexes.Count; i++)
            {
                var link = links[componentLinkIndexes[i]];
                componentNodeIndexes.Add(link.StartNodeIndex);
                componentNodeIndexes.Add(link.EndNodeIndex);
            }

            foreach (var nodeIndex in componentNodeIndexes)
            {
                var degree = nodeDegrees[nodeIndex];
                if (degree == 1)
                {
                    endpointNodeIndexes.Add(nodeIndex);
                    continue;
                }

                if (degree == 2)
                    continue;

                error = "所选墙面线未能组成开放链或闭合环。";
                return false;
            }

            if (endpointNodeIndexes.Count == 0)
            {
                return TryBuildClosedLoopGuideSelectionsForComponent(
                    links,
                    nodeLinks,
                    componentLinkIndexes,
                    elevation,
                    out guideSelections,
                    out error);
            }

            if (endpointNodeIndexes.Count != 2)
            {
                error = "所选墙面线未能组成开放链，请不要选择分叉对象。";
                return false;
            }

            var componentVisitedLinks = new HashSet<int>();
            var orderedPoints = new List<Point2d>();
            var currentNodeIndex = endpointNodeIndexes[0];
            ObjectId propertySourceEntityId = ObjectId.Null;

            while (true)
            {
                var nextLinkIndex = -1;
                var adjacentLinks = nodeLinks[currentNodeIndex];
                for (var i = 0; i < adjacentLinks.Count; i++)
                {
                    var candidateLinkIndex = adjacentLinks[i];
                    if (!componentLinkIndexes.Contains(candidateLinkIndex) || componentVisitedLinks.Contains(candidateLinkIndex))
                        continue;

                    nextLinkIndex = candidateLinkIndex;
                    break;
                }

                if (nextLinkIndex < 0)
                    break;

                componentVisitedLinks.Add(nextLinkIndex);
                var nextLink = links[nextLinkIndex];
                var useForwardOrder = nextLink.StartNodeIndex == currentNodeIndex;
                GeometryHelpers.AppendPartVertices(orderedPoints, nextLink.Part, reverse: !useForwardOrder);
                if (propertySourceEntityId.IsNull)
                    propertySourceEntityId = nextLink.Part.EntityId;

                currentNodeIndex = useForwardOrder
                    ? nextLink.EndNodeIndex
                    : nextLink.StartNodeIndex;
            }

            if (componentVisitedLinks.Count != componentLinkIndexes.Count || orderedPoints.Count < 2)
            {
                error = "所选墙面线不是有效的开放链。";
                return false;
            }

            guideSelections.Add(new GuideChainSelection(
                propertySourceEntityId,
                GeometryHelpers.CreateGuidePolyline(orderedPoints, elevation)));
            requiresRecognizedFigureSigns = true;
            return true;

            void AddAdjacentLinks(int nodeIndex)
            {
                var adjacentLinks = nodeLinks[nodeIndex];
                for (var i = 0; i < adjacentLinks.Count; i++)
                {
                    var adjacentLinkIndex = adjacentLinks[i];
                    if (!globalVisitedLinks[adjacentLinkIndex])
                        stack.Push(adjacentLinkIndex);
                }
            }
        }

        private static bool TryBuildClosedLoopGuideSelectionsForComponent(
            IReadOnlyList<SourceChainLink> links,
            IReadOnlyList<List<int>> nodeLinks,
            IReadOnlyList<int> componentLinkIndexes,
            double elevation,
            out List<GuideChainSelection> guideSelections,
            out string error)
        {
            guideSelections = new List<GuideChainSelection>(componentLinkIndexes.Count);
            error = "";

            if (componentLinkIndexes.Count == 0)
            {
                error = "未识别到闭合柱子轮廓。";
                return false;
            }

            var componentLinkIndexSet = new HashSet<int>(componentLinkIndexes);
            var orderedLinks = new List<(SourceChainLink Link, bool Reverse)>(componentLinkIndexes.Count);
            var visitedLinkIndexes = new HashSet<int>();
            var firstLink = links[componentLinkIndexes[0]];
            orderedLinks.Add((firstLink, false));
            visitedLinkIndexes.Add(componentLinkIndexes[0]);

            var startNodeIndex = firstLink.StartNodeIndex;
            var currentNodeIndex = firstLink.EndNodeIndex;

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
                {
                    DisposeGuideSelections(guideSelections);
                    guideSelections = new List<GuideChainSelection>();
                    error = "所选闭合线未能组成有效柱子轮廓。";
                    return false;
                }

                var nextLink = links[nextLinkIndex];
                var useForwardOrder = nextLink.StartNodeIndex == currentNodeIndex;
                orderedLinks.Add((nextLink, Reverse: !useForwardOrder));
                visitedLinkIndexes.Add(nextLinkIndex);
                currentNodeIndex = useForwardOrder
                    ? nextLink.EndNodeIndex
                    : nextLink.StartNodeIndex;
            }

            if (currentNodeIndex != startNodeIndex)
            {
                error = "所选闭合线未能组成有效柱子轮廓。";
                return false;
            }

            var loopVertices = new List<Point2d>();
            for (var i = 0; i < orderedLinks.Count; i++)
                GeometryHelpers.AppendPartVertices(loopVertices, orderedLinks[i].Link.Part, orderedLinks[i].Reverse);
            var hatchBoundaryEntityIds = orderedLinks
                .Select(x => x.Link.Part.EntityId)
                .Where(x => !x.IsNull)
                .Distinct()
                .ToArray();

            var guideSelection = CreateClosedLoopGuideSelection(
                orderedLinks[0].Link.Part.EntityId,
                loopVertices,
                elevation,
                hatchBoundaryEntityIds);
            if (guideSelection == null)
            {
                error = "闭合柱子轮廓点数不足。";
                return false;
            }

            guideSelections.Add(guideSelection);
            return true;
        }

        private static Point3d ComputeLoopCenter(IReadOnlyList<Point2d> vertices, double elevation)
        {
            if (vertices.Count == 0)
                return new Point3d(0.0, 0.0, elevation);

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            for (var i = 0; i < vertices.Count; i++)
            {
                var point = vertices[i];
                if (point.X < minX) minX = point.X;
                if (point.Y < minY) minY = point.Y;
                if (point.X > maxX) maxX = point.X;
                if (point.Y > maxY) maxY = point.Y;
            }

            return new Point3d((minX + maxX) * 0.5, (minY + maxY) * 0.5, elevation);
        }

        internal static GuideChainSelection? CreateClosedLoopGuideSelection(
            ObjectId propertySourceEntityId,
            IReadOnlyList<Point2d> rawVertices,
            double elevation,
            IReadOnlyList<ObjectId>? hatchBoundaryEntityIds = null)
        {
            var vertices = NormalizeClosedLoopVertices(rawVertices);
            if (vertices.Count < 3)
                return null;

            var centerPoint = ComputeLoopCenter(vertices, elevation);
            var guidePolyline = GeometryHelpers.CreateGuidePolyline(vertices, elevation, closed: true);
            var sideSign = -GeometryHelpers.ResolveSideSign(guidePolyline, centerPoint, 1);
            return new GuideChainSelection(
                propertySourceEntityId,
                guidePolyline,
                sideSign,
                useForDirectionResolution: false,
                hatchBoundaryEntityIds: hatchBoundaryEntityIds);
        }

        private static List<Point2d> NormalizeClosedLoopVertices(IReadOnlyList<Point2d> rawVertices)
        {
            var vertices = new List<Point2d>(rawVertices.Count);
            for (var i = 0; i < rawVertices.Count; i++)
                GeometryHelpers.AddVertexIfDistinct(vertices, rawVertices[i]);

            if (vertices.Count > 1 && GeometryHelpers.ArePointsEqual(vertices[0], vertices[^1]))
                vertices.RemoveAt(vertices.Count - 1);

            return vertices;
        }
    }
}
