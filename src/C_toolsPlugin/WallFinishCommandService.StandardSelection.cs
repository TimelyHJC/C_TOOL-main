using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static partial class StandardSelectionBuilder
    {
        internal static bool TryCreateSelection(
            Database db,
            IReadOnlyList<ObjectId> entityIds,
            out SourceCurveSelection? selection,
            out string error)
        {
            selection = null;
            error = "";

            if (entityIds.Count == 0)
            {
                error = "未选择墙面线对象。";
                return false;
            }

            var selectionError = "";
            var parts = CadDatabaseScope.Read(
                db,
                (_, tr) =>
                {
                    var chainParts = new List<SourceChainPart>(entityIds.Count);
                    for (var index = 0; index < entityIds.Count; index++)
                    {
                        var entityId = entityIds[index];
                        if (!TryCreateChainPart(tr, entityId, out var part, out selectionError) || part == null)
                            return null;

                        chainParts.Add(part);
                    }

                    return chainParts;
                });

            if (parts == null)
            {
                error = selectionError;
                return false;
            }

            if (!TryBuildGuideSelections(parts, out var guideSelections, out selectionError) ||
                guideSelections.Count == 0)
            {
                error = selectionError;
                return false;
            }

            selection = new SourceCurveSelection(guideSelections);
            return true;
        }

        private static bool TryCreateChainPart(
            Transaction tr,
            ObjectId entityId,
            out SourceChainPart? part,
            out string error)
        {
            part = null;
            error = "";

            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, entityId, OpenMode.ForRead, out var entity) ||
                entity == null)
            {
                error = "未找到可用的墙面线对象。";
                return false;
            }

            switch (entity)
            {
                case Line line:
                    if (line.Length <= PointTolerance)
                    {
                        error = "所选直线长度必须大于 0。";
                        return false;
                    }

                    if (!GeometryHelpers.IsWorldXyPlane(line.Normal) || Math.Abs(line.StartPoint.Z - line.EndPoint.Z) > PointTolerance)
                    {
                        error = "目前仅支持世界坐标 XY 平面内的墙面线。";
                        return false;
                    }

                    part = new SourceChainPart(
                        entityId,
                        line.StartPoint.Z,
                        new List<Point2d>
                        {
                            new(line.StartPoint.X, line.StartPoint.Y),
                            new(line.EndPoint.X, line.EndPoint.Y)
                        },
                        isClosed: false);
                    return true;

                case Arc arc:
                    if (arc.Radius <= PointTolerance || arc.Length <= PointTolerance)
                    {
                        error = "所选圆弧长度必须大于 0。";
                        return false;
                    }

                    if (!GeometryHelpers.IsWorldXyPlane(arc.Normal))
                    {
                        error = "目前仅支持世界坐标 XY 平面内的墙面线。";
                        return false;
                    }

                    var arcVertices = GeometryHelpers.CreateArcApproximationPoints(arc);
                    if (arcVertices.Count < 2)
                    {
                        error = "未能读取圆弧墙面线。";
                        return false;
                    }

                    part = new SourceChainPart(entityId, arc.Center.Z, arcVertices, isClosed: false);
                    return true;

                case Polyline polyline:
                    if (polyline.NumberOfVertices < 2 || polyline.Length <= PointTolerance)
                    {
                        error = polyline.Closed
                            ? "闭合多段线至少需要两个有效顶点。"
                            : "开放多段线至少需要两个有效顶点。";
                        return false;
                    }

                    if (!GeometryHelpers.IsWorldXyPlane(polyline.Normal))
                    {
                        error = "目前仅支持世界坐标 XY 平面内的墙面线。";
                        return false;
                    }

                    var vertices = GeometryHelpers.CreatePolylineApproximationPoints(polyline);
                    var minimumVertexCount = polyline.Closed ? 3 : 2;
                    if (vertices.Count < minimumVertexCount)
                    {
                        error = polyline.Closed
                            ? "闭合多段线至少需要三个有效点。"
                            : "开放多段线至少需要两个有效点。";
                        return false;
                    }

                    part = new SourceChainPart(entityId, polyline.Elevation, vertices, polyline.Closed);
                    return true;

                default:
                    error = "仅支持直线、圆弧或多段线。";
                    return false;
            }
        }

        private static bool TryBuildGuideSelections(
            IReadOnlyList<SourceChainPart> parts,
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

            var recognizedGuideSelections = new List<GuideChainSelection>();
            var openParts = new List<SourceChainPart>(parts.Count);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (part.IsClosed)
                {
                    AddClosedLoopGuides(guideSelections, part);
                    continue;
                }

                openParts.Add(part);
            }

            if (openParts.Count == 0)
                return guideSelections.Count > 0;

            if (openParts.Count == 1)
            {
                recognizedGuideSelections.Add(new GuideChainSelection(
                    openParts[0].EntityId,
                    GeometryHelpers.CreateGuidePolyline(openParts[0].Vertices, elevation)));
                ApplyRecognizedFigureInteriorSigns(recognizedGuideSelections);
                guideSelections.AddRange(recognizedGuideSelections);
                return true;
            }

            var nodePoints = new List<Point2d>();
            var nodeDegrees = new List<int>();
            var nodeLinks = new List<List<int>>();
            var links = new List<SourceChainLink>(openParts.Count);

            for (var i = 0; i < openParts.Count; i++)
            {
                var part = openParts[i];
                var startNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, part.StartPoint);
                var endNodeIndex = GeometryHelpers.FindOrCreateNodeIndex(nodePoints, nodeDegrees, nodeLinks, part.EndPoint);
                if (startNodeIndex == endNodeIndex)
                {
                    error = "不支持自闭合的墙面线对象。";
                    return false;
                }

                var linkIndex = links.Count;
                links.Add(new SourceChainLink(part, startNodeIndex, endNodeIndex));
                nodeDegrees[startNodeIndex]++;
                nodeDegrees[endNodeIndex]++;
                nodeLinks[startNodeIndex].Add(linkIndex);
                nodeLinks[endNodeIndex].Add(linkIndex);

                if (nodeDegrees[startNodeIndex] > 2 || nodeDegrees[endNodeIndex] > 2)
                {
                    error = "所选墙面线存在分叉，F_WCC 仅支持开放链或多段彼此断开的开放链。";
                    return false;
                }
            }

            var globalVisitedLinks = new bool[links.Count];
            for (var i = 0; i < links.Count; i++)
            {
                if (globalVisitedLinks[i])
                    continue;

                if (!TryBuildGuideSelectionsForComponent(
                        links,
                        nodeDegrees,
                        nodeLinks,
                        globalVisitedLinks,
                        i,
                        elevation,
                        out var componentGuideSelections,
                        out var requiresRecognizedFigureSigns,
                        out error) || componentGuideSelections.Count == 0)
                {
                    DisposeGuideSelections(guideSelections);
                    DisposeGuideSelections(recognizedGuideSelections);
                    guideSelections.Clear();
                    recognizedGuideSelections.Clear();
                    return false;
                }

                if (requiresRecognizedFigureSigns)
                    recognizedGuideSelections.AddRange(componentGuideSelections);
                else
                    guideSelections.AddRange(componentGuideSelections);
            }

            ApplyRecognizedFigureInteriorSigns(recognizedGuideSelections);
            guideSelections.AddRange(recognizedGuideSelections);
            return guideSelections.Count > 0;
        }

        private static void AddClosedLoopGuides(List<GuideChainSelection> guideSelections, SourceChainPart part)
        {
            if (!part.IsClosed || part.Vertices.Count < 2)
                return;

            var guideSelection = CreateClosedLoopGuideSelection(
                part.EntityId,
                part.Vertices,
                part.Elevation,
                new[] { part.EntityId });
            if (guideSelection != null)
                guideSelections.Add(guideSelection);
        }

        private static void ApplyRecognizedFigureInteriorSigns(List<GuideChainSelection> guideSelections)
        {
            if (!CanRecognizeFigureInterior(guideSelections))
                return;

            var recognizedCenter = ComputeRecognizedFigureCenter(guideSelections);
            for (var i = 0; i < guideSelections.Count; i++)
            {
                var current = guideSelections[i];
                guideSelections[i] = new GuideChainSelection(
                    current.PropertySourceEntityId,
                    current.GuidePolyline,
                    GeometryHelpers.ResolveSideSign(current.GuidePolyline, recognizedCenter, 1),
                    current.UseForDirectionResolution);
            }
        }

        internal static bool CanRecognizeFigureInterior(IReadOnlyList<GuideChainSelection> guideSelections)
        {
            if (guideSelections.Count < 2)
                return false;

            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            var found = false;

            for (var i = 0; i < guideSelections.Count; i++)
            {
                var guidePolyline = guideSelections[i].GuidePolyline;
                for (var vertexIndex = 0; vertexIndex < guidePolyline.NumberOfVertices; vertexIndex++)
                {
                    var point = guidePolyline.GetPoint3dAt(vertexIndex);
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
            }

            if (!found)
                return false;

            var totalWidth = maxX - minX;
            var totalHeight = maxY - minY;
            if (totalWidth <= PointTolerance || totalHeight <= PointTolerance)
                return false;

            var hasWideChain = false;
            var hasTallChain = false;
            for (var i = 0; i < guideSelections.Count; i++)
            {
                var guidePolyline = guideSelections[i].GuidePolyline;
                if (!TryGetPolylineBounds(guidePolyline, out var chainMinX, out var chainMinY, out var chainMaxX, out var chainMaxY))
                    continue;

                var chainWidth = chainMaxX - chainMinX;
                var chainHeight = chainMaxY - chainMinY;
                if (chainWidth >= totalWidth * RecognizedFigureWideCoverageRatio)
                    hasWideChain = true;
                if (chainHeight >= totalHeight * RecognizedFigureTallCoverageRatio)
                    hasTallChain = true;

                if (hasWideChain && hasTallChain)
                    return true;
            }

            return false;
        }

        private static bool TryGetPolylineBounds(
            Polyline polyline,
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

            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var point = polyline.GetPoint3dAt(i);
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

        internal static Point3d ComputeRecognizedFigureCenter(IReadOnlyList<GuideChainSelection> guideSelections)
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var minZ = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            var maxZ = double.MinValue;
            var found = false;

            for (var i = 0; i < guideSelections.Count; i++)
            {
                var guidePolyline = guideSelections[i].GuidePolyline;
                for (var vertexIndex = 0; vertexIndex < guidePolyline.NumberOfVertices; vertexIndex++)
                {
                    var point = guidePolyline.GetPoint3dAt(vertexIndex);
                    if (!found)
                    {
                        minX = maxX = point.X;
                        minY = maxY = point.Y;
                        minZ = maxZ = point.Z;
                        found = true;
                        continue;
                    }

                    if (point.X < minX) minX = point.X;
                    if (point.Y < minY) minY = point.Y;
                    if (point.Z < minZ) minZ = point.Z;
                    if (point.X > maxX) maxX = point.X;
                    if (point.Y > maxY) maxY = point.Y;
                    if (point.Z > maxZ) maxZ = point.Z;
                }
            }

            return found
                ? new Point3d((minX + maxX) * 0.5, (minY + maxY) * 0.5, (minZ + maxZ) * 0.5)
                : Point3d.Origin;
        }
    }
}
