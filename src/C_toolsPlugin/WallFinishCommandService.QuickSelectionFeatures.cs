using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    private static partial class QuickSelectionFeatureBuilder
    {
        internal static bool TryBuildQuickWallGuideSelections(
            IReadOnlyList<SourceChainPart> parts,
            Point3d interiorSamplePoint,
            out List<GuideChainSelection> guideSelections,
            out string error)
        {
            guideSelections = new List<GuideChainSelection>();
            error = "";

            if (parts.Count == 0)
            {
                error = "未找到可用的墙面线对象。";
                return false;
            }

            var elevation = parts[0].Elevation;
            for (var i = 0; i < parts.Count; i++)
            {
                if (Math.Abs(parts[i].Elevation - elevation) > PointTolerance)
                {
                    error = "所选墙面线必须位于同一标高平面。";
                    return false;
                }
            }

            if (parts.Count == 1)
            {
                var guidePolyline = GeometryHelpers.CreateGuidePolyline(parts[0].Vertices, elevation);
                var sideSign = GeometryHelpers.ResolveSideSign(guidePolyline, interiorSamplePoint, 1);
                guideSelections.Add(new GuideChainSelection(parts[0].EntityId, guidePolyline, sideSign));
                return true;
            }

            var nodePoints = new List<Point2d>();
            var nodeDegrees = new List<int>();
            var nodeLinks = new List<List<int>>();
            var links = new List<SourceChainLink>(parts.Count);

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                var startNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, part.StartPoint);
                var endNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, part.EndPoint);
                if (startNodeIndex == endNodeIndex)
                    continue;

                var linkIndex = links.Count;
                links.Add(new SourceChainLink(part, startNodeIndex, endNodeIndex));
                nodeDegrees[startNodeIndex]++;
                nodeDegrees[endNodeIndex]++;
                nodeLinks[startNodeIndex].Add(linkIndex);
                nodeLinks[endNodeIndex].Add(linkIndex);
            }

            var globalVisitedLinks = new bool[links.Count];
            for (var i = 0; i < links.Count; i++)
            {
                if (globalVisitedLinks[i])
                    continue;

                if (!TryBuildQuickGuideComponent(
                        links,
                        nodeDegrees,
                        nodeLinks,
                        globalVisitedLinks,
                        i,
                        elevation,
                        out var propertySourceEntityId,
                        out var guidePolyline,
                        out error) || guidePolyline == null)
                {
                    DisposeGuideSelections(guideSelections);
                    return false;
                }

                var sideSign = GeometryHelpers.ResolveSideSign(guidePolyline, interiorSamplePoint, 1);
                guideSelections.Add(new GuideChainSelection(propertySourceEntityId, guidePolyline, sideSign));
            }

            return guideSelections.Count > 0;
        }

        private static bool TryBuildQuickGuideComponent(
            IReadOnlyList<SourceChainLink> links,
            IReadOnlyList<int> nodeDegrees,
            IReadOnlyList<List<int>> nodeLinks,
            bool[] globalVisitedLinks,
            int seedLinkIndex,
            double elevation,
            out ObjectId propertySourceEntityId,
            out Polyline? guidePolyline,
            out string error)
        {
            propertySourceEntityId = ObjectId.Null;
            guidePolyline = null;
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

            var componentLinkIndexSet = new HashSet<int>(componentLinkIndexes);
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

                error = "识别到分叉墙线，无法自动生成。";
                return false;
            }

            var componentVisitedLinks = new HashSet<int>();
            var orderedPoints = new List<Point2d>();
            var isClosedComponent = false;
            if (endpointNodeIndexes.Count == 2)
            {
                var currentNodeIndex = endpointNodeIndexes[0];
                while (true)
                {
                    var nextLinkIndex = -1;
                    var adjacentLinks = nodeLinks[currentNodeIndex];
                    for (var i = 0; i < adjacentLinks.Count; i++)
                    {
                        var candidateLinkIndex = adjacentLinks[i];
                        if (!componentLinkIndexSet.Contains(candidateLinkIndex) || componentVisitedLinks.Contains(candidateLinkIndex))
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
            }
            else if (endpointNodeIndexes.Count == 0)
            {
                isClosedComponent = true;
                var firstLinkIndex = componentLinkIndexes[0];
                componentVisitedLinks.Add(firstLinkIndex);
                var firstLink = links[firstLinkIndex];
                propertySourceEntityId = firstLink.Part.EntityId;
                GeometryHelpers.AppendPartVertices(orderedPoints, firstLink.Part, reverse: false);

                var startNodeIndex = firstLink.StartNodeIndex;
                var currentNodeIndex = firstLink.EndNodeIndex;
                while (currentNodeIndex != startNodeIndex)
                {
                    var nextLinkIndex = -1;
                    var adjacentLinks = nodeLinks[currentNodeIndex];
                    for (var i = 0; i < adjacentLinks.Count; i++)
                    {
                        var candidateLinkIndex = adjacentLinks[i];
                        if (!componentLinkIndexSet.Contains(candidateLinkIndex) || componentVisitedLinks.Contains(candidateLinkIndex))
                            continue;

                        nextLinkIndex = candidateLinkIndex;
                        break;
                    }

                    if (nextLinkIndex < 0)
                    {
                        error = "识别到未闭合的外墙环。";
                        return false;
                    }

                    componentVisitedLinks.Add(nextLinkIndex);
                    var nextLink = links[nextLinkIndex];
                    var useForwardOrder = nextLink.StartNodeIndex == currentNodeIndex;
                    GeometryHelpers.AppendPartVertices(orderedPoints, nextLink.Part, reverse: !useForwardOrder);
                    currentNodeIndex = useForwardOrder
                        ? nextLink.EndNodeIndex
                        : nextLink.StartNodeIndex;
                }
            }
            else
            {
                error = "识别到异常墙线连接，请改用标准模式。";
                return false;
            }

            if (componentVisitedLinks.Count != componentLinkIndexes.Count || orderedPoints.Count < 2)
            {
                error = "识别到无效墙线链。";
                return false;
            }

            guidePolyline = GeometryHelpers.CreateGuidePolyline(orderedPoints, elevation, isClosedComponent);
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

        internal static List<GuideChainSelection> CreateFallbackQuickWallGuides(
            IReadOnlyList<QuickWallGuideCandidate> wallSections,
            Point3d interiorSamplePoint)
        {
            var guideSelections = new List<GuideChainSelection>(wallSections.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < wallSections.Count; i++)
            {
                var key = GeometryHelpers.GetQuickSegmentKey(wallSections[i].InnerSegment);
                if (!seen.Add(key))
                    continue;

                var guidePolyline = GeometryHelpers.CreateGuidePolyline(
                    new[]
                    {
                        wallSections[i].InnerSegment.StartPoint,
                        wallSections[i].InnerSegment.EndPoint
                    },
                    wallSections[i].InnerSegment.Elevation);
                var sideSign = GeometryHelpers.ResolveSideSign(guidePolyline, interiorSamplePoint, 1);
                guideSelections.Add(new GuideChainSelection(
                    wallSections[i].InnerSegment.SourceEntityId,
                    guidePolyline,
                    sideSign));
            }

            return guideSelections;
        }
    }
}
