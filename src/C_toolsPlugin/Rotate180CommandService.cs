using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsPlugin;

/// <summary>
/// F_SSR：按所选对象包围盒中心旋转 180 度。
/// </summary>
internal static class Rotate180CommandService
{
    private const string CommandName = PluginCommandIds.Rotate180;
    private const double GeometryTolerance = 1e-9;

    internal static void Run()
    {
        CadCommandContext.ExecuteInActiveDocument($"执行 {CommandName}", (doc, ed) =>
        {
            if (!TryGetSelectionIds(ed, out var selectedIds, out var cancelled))
            {
                ed.WriteMessage(cancelled
                    ? $"\nC_TOOL：{CommandName} 已取消。"
                    : $"\nC_TOOL：{CommandName} 未选择任何对象。");
                return;
            }

            var targetIds = Array.Empty<ObjectId>();
            var centerPoint = Point3d.Origin;
            var error = "";
            var hasCenter = CadDatabaseScope.Read(doc, (_, tr) =>
                TryGetSelectionCenter(
                    tr,
                    ed.CurrentUserCoordinateSystem,
                    selectedIds,
                    out targetIds,
                    out centerPoint,
                    out error));
            if (!hasCenter)
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{error}");
                return;
            }

            var rotatedCount = 0;
            error = "";
            CadDatabaseScope.Write(doc, (_, tr) =>
            {
                var ucs = ed.CurrentUserCoordinateSystem.CoordinateSystem3d;
                if (ucs.Zaxis.Length <= GeometryTolerance)
                {
                    error = "当前 UCS 的旋转轴无效。";
                    return;
                }

                var transform = Matrix3d.Rotation(Math.PI, ucs.Zaxis.GetNormal(), centerPoint);
                TryRotateTargets(tr, targetIds, transform, out rotatedCount, out error);
            }, requireDocumentLock: true);

            if (error.Length > 0)
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{error}");
                return;
            }

            if (rotatedCount <= 0)
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 未找到可旋转的对象。");
                return;
            }

            ed.WriteMessage($"\nC_TOOL：{CommandName} 已按所选对象中心旋转 {rotatedCount} 个对象 180 度。");
            ed.SetImpliedSelection(targetIds);
        });
    }

    private static bool TryGetSelectionIds(Editor ed, out ObjectId[] ids, out bool cancelled)
    {
        ids = Array.Empty<ObjectId>();
        cancelled = false;

        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            ids = NormalizeSelectionIds(implied.Value.GetObjectIds());
            return ids.Length > 0;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nC_TOOL：选择要旋转 180 度的对象："
        };
        var result = ed.GetSelection(options);
        if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
        {
            cancelled = result.Status == PromptStatus.Cancel;
            return false;
        }

        ids = NormalizeSelectionIds(result.Value.GetObjectIds());
        return ids.Length > 0;
    }

    private static bool TryGetSelectionCenter(
        Transaction tr,
        Matrix3d currentUcs,
        ObjectId[] selectedIds,
        out ObjectId[] targetIds,
        out Point3d centerPoint,
        out string error)
    {
        targetIds = Array.Empty<ObjectId>();
        centerPoint = Point3d.Origin;
        error = "";

        if (selectedIds.Length == 0)
        {
            error = "没有可参与旋转的对象。";
            return false;
        }

        var wcsToUcs = currentUcs.Inverse();
        var targetList = new List<ObjectId>(selectedIds.Length);
        var bounds = BoundsAccumulator.Empty;

        for (var index = 0; index < selectedIds.Length; index++)
        {
            var objectId = selectedIds[index];
            if (objectId.IsNull)
                continue;

            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
                entity == null ||
                entity.IsErased)
            {
                continue;
            }

            if (!TryGetEntityExtents(entity, out var extents))
                continue;

            foreach (var corner in GetExtentsCorners(extents))
            {
                bounds.Add(corner.TransformBy(wcsToUcs));
            }

            targetList.Add(objectId);
        }

        if (!bounds.HasValue)
        {
            error = "无法计算所选对象的中心点。";
            return false;
        }

        targetIds = targetList.ToArray();
        centerPoint = bounds.Center.TransformBy(currentUcs);
        return targetIds.Length > 0;
    }

    private static bool TryRotateTargets(
        Transaction tr,
        ObjectId[] targetIds,
        Matrix3d transform,
        out int rotatedCount,
        out string error)
    {
        rotatedCount = 0;
        error = "";
        var rotatableIds = new List<ObjectId>(targetIds.Length);

        for (var index = 0; index < targetIds.Length; index++)
        {
            var objectId = targetIds[index];
            if (objectId.IsNull)
                continue;

            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
                entity == null ||
                entity.IsErased)
            {
                continue;
            }

            if (CadDatabaseScope.IsOnLockedLayer(tr, entity))
            {
                error = $"对象所在图层“{entity.Layer}”已锁定，未执行旋转。";
                return false;
            }

            rotatableIds.Add(objectId);
        }

        if (rotatableIds.Count <= 0)
        {
            error = "未找到可旋转的对象。";
            return false;
        }

        for (var index = 0; index < rotatableIds.Count; index++)
        {
            var objectId = rotatableIds[index];
            if (!CadDatabaseScope.TryOpenAs<Entity>(tr, objectId, OpenMode.ForRead, out var entity) ||
                entity == null ||
                entity.IsErased)
            {
                continue;
            }

            if (!entity.IsWriteEnabled)
                entity.UpgradeOpen();

            entity.TransformBy(transform);
            rotatedCount++;
        }

        if (rotatedCount <= 0)
        {
            error = "未找到可旋转的对象。";
            return false;
        }

        return true;
    }

    private static bool TryGetEntityExtents(Entity entity, out Extents3d extents)
    {
        extents = default;

        try
        {
            extents = entity.GeometricExtents;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (AcadRuntimeException)
        {
            return false;
        }
    }

    private static IEnumerable<Point3d> GetExtentsCorners(Extents3d extents)
    {
        var min = extents.MinPoint;
        var max = extents.MaxPoint;

        yield return new Point3d(min.X, min.Y, min.Z);
        yield return new Point3d(min.X, min.Y, max.Z);
        yield return new Point3d(min.X, max.Y, min.Z);
        yield return new Point3d(min.X, max.Y, max.Z);
        yield return new Point3d(max.X, min.Y, min.Z);
        yield return new Point3d(max.X, min.Y, max.Z);
        yield return new Point3d(max.X, max.Y, min.Z);
        yield return new Point3d(max.X, max.Y, max.Z);
    }

    private static ObjectId[] NormalizeSelectionIds(IEnumerable<ObjectId> rawIds)
    {
        return rawIds
            .Where(static id => !id.IsNull)
            .Distinct()
            .ToArray();
    }

    private struct BoundsAccumulator
    {
        private double _minX;
        private double _minY;
        private double _minZ;
        private double _maxX;
        private double _maxY;
        private double _maxZ;

        internal static BoundsAccumulator Empty => default;

        internal bool HasValue { get; private set; }

        internal Point3d Center => new(
            (_minX + _maxX) * 0.5,
            (_minY + _maxY) * 0.5,
            (_minZ + _maxZ) * 0.5);

        internal void Add(Point3d point)
        {
            if (!HasValue)
            {
                _minX = _maxX = point.X;
                _minY = _maxY = point.Y;
                _minZ = _maxZ = point.Z;
                HasValue = true;
                return;
            }

            _minX = Math.Min(_minX, point.X);
            _minY = Math.Min(_minY, point.Y);
            _minZ = Math.Min(_minZ, point.Z);
            _maxX = Math.Max(_maxX, point.X);
            _maxY = Math.Max(_maxY, point.Y);
            _maxZ = Math.Max(_maxZ, point.Z);
        }
    }
}
