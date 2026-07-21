using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace C_toolsPlugin;

/// <summary>
/// F_JX：将多个矩形合并为一个闭合图形。
/// </summary>
internal static class RectangleMergeCommandService
{
    private const string CommandName = PluginCommandIds.RectangleMerge;
    private const double GeometryTolerance = 1e-6;

    internal static void Run()
    {
        CadCommandContext.ExecuteInActiveDocument($"执行 {CommandName}", (doc, ed) =>
        {
            if (!TryGetSelectionIds(ed, out var selectedIds, out var cancelled))
            {
                ed.WriteMessage(cancelled
                    ? $"\n{UIMessages.Prefix_C_TOOL}{CommandName} 已取消。"
                    : $"\n{UIMessages.Prefix_C_TOOL}{CommandName} 未选择任何对象。");
                return;
            }

            var rectangles = new List<RectangleMergeGeometry.RectangleFootprint>();
            var skippedCount = 0;
            var collectionError = "";

            CadDatabaseScope.Read(doc, (_, tr) =>
            {
                CollectRectangles(tr, selectedIds, rectangles, ref skippedCount);
            });

            if (rectangles.Count == 0)
            {
                ed.WriteMessage(skippedCount > 0
                    ? $"\n{UIMessages.Prefix_C_TOOL}{CommandName} 未找到可合并的矩形，已跳过 {skippedCount} 个对象。"
                    : $"\n{UIMessages.Prefix_C_TOOL}{CommandName} 未找到可合并的矩形。");
                return;
            }

            if (!RectangleMergeGeometry.TryBuildMergedBoundaryLoops(rectangles, out var loops, out collectionError))
            {
                ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{CommandName} {collectionError}");
                return;
            }

            var createdCount = 0;
            CadDatabaseScope.Write(doc, (db, tr) =>
            {
                var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(db, tr);
                for (var index = 0; index < loops.Count; index++)
                {
                    if (loops[index].Count < 3)
                        continue;

                    var polyline = CreateClosedPolyline(loops[index], rectangles[0].Corners[0].Z);
                    currentSpace.AppendEntity(polyline);
                    tr.AddNewlyCreatedDBObject(polyline, add: true);
                    createdCount++;
                }
            }, requireDocumentLock: true);

            var skippedText = skippedCount > 0 ? $"，已跳过 {skippedCount} 个非矩形对象" : "";
            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{CommandName} 已合并 {rectangles.Count} 个矩形，生成 {createdCount} 个闭合图形{skippedText}。");
        });
    }

    private static void CollectRectangles(
        Transaction tr,
        IReadOnlyList<ObjectId> selectedIds,
        List<RectangleMergeGeometry.RectangleFootprint> rectangles,
        ref int skippedCount)
    {
        for (var index = 0; index < selectedIds.Count; index++)
        {
            var objectId = selectedIds[index];
            if (objectId.IsNull)
            {
                skippedCount++;
                continue;
            }

            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) || entity == null)
            {
                skippedCount++;
                continue;
            }

            if (TryCreateRectangleFootprint(entity, tr, out var footprint))
            {
                rectangles.Add(footprint);
                continue;
            }

            skippedCount++;
        }
    }

    private static bool TryCreateRectangleFootprint(
        Entity entity,
        Transaction tr,
        out RectangleMergeGeometry.RectangleFootprint footprint)
    {
        footprint = default;

        return entity switch
        {
            Polyline polyline => TryCreatePolylineFootprint(polyline, out footprint),
            Polyline2d polyline2d => TryCreatePolyline2dFootprint(tr, polyline2d, out footprint),
            Polyline3d polyline3d => TryCreatePolyline3dFootprint(tr, polyline3d, out footprint),
            _ => false
        };
    }

    private static bool TryCreatePolylineFootprint(
        Polyline polyline,
        out RectangleMergeGeometry.RectangleFootprint footprint)
    {
        footprint = default;

        if (!polyline.Closed || polyline.NumberOfVertices != 4)
            return false;

        if (!TryCollectPolylinePoints(polyline, out var points))
            return false;

        if (!TryCreateFootprint(points, out footprint))
            return false;

        return true;
    }

    private static bool TryCreatePolyline2dFootprint(
        Transaction tr,
        Polyline2d polyline2d,
        out RectangleMergeGeometry.RectangleFootprint footprint)
    {
        footprint = default;

        if (!polyline2d.Closed)
            return false;

        if (!TryCollectPolyline2dPoints(tr, polyline2d, out var points))
            return false;

        return TryCreateFootprint(points, out footprint);
    }

    private static bool TryCreatePolyline3dFootprint(
        Transaction tr,
        Polyline3d polyline3d,
        out RectangleMergeGeometry.RectangleFootprint footprint)
    {
        footprint = default;

        if (!polyline3d.Closed)
            return false;

        if (!TryCollectPolyline3dPoints(tr, polyline3d, out var points))
            return false;

        return TryCreateFootprint(points, out footprint);
    }

    private static bool TryCreateFootprint(
        IReadOnlyList<Point3d> points,
        out RectangleMergeGeometry.RectangleFootprint footprint)
    {
        footprint = default;
        if (points.Count != 4)
            return false;

        if (!TryValidateRectangleOrder(points))
            return false;

        footprint = RectangleMergeGeometry.CreateFootprint(points.ToArray());
        return true;
    }

    private static bool TryValidateRectangleOrder(IReadOnlyList<Point3d> points)
    {
        for (var index = 0; index < points.Count; index++)
        {
            if (Math.Abs(points[index].Z - points[0].Z) > GeometryTolerance)
                return false;
        }

        for (var index = 0; index < points.Count; index++)
        {
            var current = points[index];
            var next = points[(index + 1) % points.Count];
            var edge = next - current;
            if (edge.Length <= GeometryTolerance)
                return false;

            if (index > 0)
            {
                var previousEdge = current - points[index - 1];
                if (Math.Abs(previousEdge.DotProduct(edge)) > GeometryTolerance * 100.0)
                    return false;
            }
        }

        return true;
    }

    private static bool TryCollectPolylinePoints(Polyline polyline, out List<Point3d> points)
    {
        points = new List<Point3d>(4);

        for (var index = 0; index < polyline.NumberOfVertices; index++)
        {
            if (Math.Abs(polyline.GetBulgeAt(index)) > GeometryTolerance)
                return false;

            AddDistinctPoint(points, polyline.GetPoint3dAt(index));
        }

        return points.Count == 4;
    }

    private static bool TryCollectPolyline2dPoints(Transaction tr, Polyline2d polyline2d, out List<Point3d> points)
    {
        points = new List<Point3d>(4);

        foreach (ObjectId vertexId in polyline2d)
        {
            if (!CadDatabaseScope.TryOpenAs<Vertex2d>(tr, vertexId, OpenMode.ForRead, out var vertex) || vertex == null)
                return false;

            if (Math.Abs(vertex.Bulge) > GeometryTolerance)
                return false;

            AddDistinctPoint(points, vertex.Position);
        }

        return points.Count == 4;
    }

    private static bool TryCollectPolyline3dPoints(Transaction tr, Polyline3d polyline3d, out List<Point3d> points)
    {
        points = new List<Point3d>(4);

        foreach (ObjectId vertexId in polyline3d)
        {
            if (!CadDatabaseScope.TryOpenAs<PolylineVertex3d>(tr, vertexId, OpenMode.ForRead, out var vertex) || vertex == null)
                return false;

            AddDistinctPoint(points, vertex.Position);
        }

        return points.Count == 4;
    }

    private static void AddDistinctPoint(List<Point3d> points, Point3d point)
    {
        if (points.Count > 0 && points[^1].DistanceTo(point) <= GeometryTolerance)
            return;

        points.Add(point);
    }

    private static Polyline CreateClosedPolyline(IReadOnlyList<Point3d> points, double elevation)
    {
        var polyline = new Polyline(points.Count);
        polyline.Normal = Vector3d.ZAxis;
        polyline.Elevation = elevation;

        for (var index = 0; index < points.Count; index++)
            polyline.AddVertexAt(index, new Point2d(points[index].X, points[index].Y), 0.0, 0.0, 0.0);

        polyline.Closed = true;
        return polyline;
    }

    private static bool TryGetSelectionIds(Editor ed, out ObjectId[] ids, out bool cancelled)
    {
        ids = Array.Empty<ObjectId>();
        cancelled = false;

        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            ids = implied.Value.GetObjectIds()
                .Where(static id => !id.IsNull)
                .Distinct()
                .ToArray();
            return ids.Length > 0;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nC_TOOL：选择要合并的矩形："
        };
        var result = ed.GetSelection(options);
        if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
        {
            cancelled = result.Status == PromptStatus.Cancel;
            return false;
        }

        ids = result.Value.GetObjectIds()
            .Where(static id => !id.IsNull)
            .Distinct()
            .ToArray();
        return ids.Length > 0;
    }
}
