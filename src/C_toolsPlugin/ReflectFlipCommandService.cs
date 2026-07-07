using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsPlugin;

/// <summary>
/// F_RF：原地镜像。按所选对象包围框中心执行左右或上下镜像，保持文字方向。
/// </summary>
internal static class ReflectFlipCommandService
{
    private const string CommandName = PluginCommandIds.ReflectFlip;
    private const string LeftRightKeyword = "Z";
    private const string UpDownKeyword = "S";
    private const string DeleteSourceYesKeyword = "_Y";

    internal static void Run()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;

        try
        {
            if (!TryGetSelectionIds(ed, out var selectedIds, out var cancelled))
            {
                ed.WriteMessage(cancelled
                    ? $"\nC_TOOL：{CommandName} 已取消。"
                    : $"\nC_TOOL：{CommandName} 未选择任何对象。");
                return;
            }

            if (!TryPromptMirrorMode(ed, out var mirrorMode, out cancelled))
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 已取消。");
                return;
            }

            if (!TryGetSelectionCenter(doc.Database, ed.CurrentUserCoordinateSystem, selectedIds, out var centerPoint, out var error))
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{error}");
                return;
            }

            if (!TryExecuteMirrorFlip(doc, selectedIds, centerPoint, mirrorMode, out var resultIds, out error))
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{error}");
                return;
            }

            ed.WriteMessage(
                mirrorMode == ReflectFlipMode.LeftRight
                    ? $"\nC_TOOL：{CommandName} 已完成原地左右镜像，文字方向保持不变。"
                    : $"\nC_TOOL：{CommandName} 已完成原地上下镜像，文字方向保持不变。");

            if (resultIds.Length > 0)
                ed.SetImpliedSelection(resultIds);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败（参数错误）", ex);
            ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行失败", ex);
            ed.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
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
            MessageForAdding = "\nC_TOOL：选择要镜像的对象："
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

    private static bool TryPromptMirrorMode(Editor ed, out ReflectFlipMode mirrorMode, out bool cancelled)
    {
        mirrorMode = ReflectFlipMode.LeftRight;
        cancelled = false;

        var options = new PromptKeywordOptions(
            "\nC_TOOL：镜像方向 [左右(Z)/上下(S)] <左右>：",
            $"{LeftRightKeyword} {UpDownKeyword}")
        {
            AllowNone = true
        };
        options.Keywords.Default = LeftRightKeyword;

        var result = ed.GetKeywords(options);
        if (result.Status == PromptStatus.Cancel)
        {
            cancelled = true;
            return false;
        }

        if (result.Status == PromptStatus.None)
            return true;

        if (result.Status != PromptStatus.OK)
            return false;

        var keyword = NormalizeKeyword(result.StringResult);
        mirrorMode = string.Equals(keyword, UpDownKeyword, StringComparison.OrdinalIgnoreCase)
            ? ReflectFlipMode.UpDown
            : ReflectFlipMode.LeftRight;
        return true;
    }

    private static bool TryGetSelectionCenter(
        Database database,
        Matrix3d currentUcs,
        ObjectId[] selectedIds,
        out Point3d centerPoint,
        out string error)
    {
        centerPoint = Point3d.Origin;
        error = string.Empty;

        if (selectedIds.Length == 0)
        {
            error = "没有可参与镜像的对象。";
            return false;
        }

        var wcsToUcs = currentUcs.Inverse();
        var hasBounds = false;
        var minX = 0.0;
        var minY = 0.0;
        var minZ = 0.0;
        var maxX = 0.0;
        var maxY = 0.0;
        var maxZ = 0.0;

        CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                foreach (var objectId in selectedIds)
                {
                    if (objectId.IsNull)
                        continue;

                    Entity? entity;
                    try
                    {
                        entity = CadDatabaseScope.TryOpenAs<Entity>(transaction, objectId, OpenMode.ForRead, out var openedEntity)
                            ? openedEntity
                            : null;
                    }
                    catch
                    {
                        continue;
                    }

                    if (entity == null || entity.IsErased)
                        continue;

                    Extents3d extents;
                    try
                    {
                        extents = entity.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    foreach (var corner in GetExtentsCorners(extents))
                    {
                        var pointInUcs = corner.TransformBy(wcsToUcs);
                        if (!hasBounds)
                        {
                            minX = maxX = pointInUcs.X;
                            minY = maxY = pointInUcs.Y;
                            minZ = maxZ = pointInUcs.Z;
                            hasBounds = true;
                            continue;
                        }

                        if (pointInUcs.X < minX)
                            minX = pointInUcs.X;
                        if (pointInUcs.X > maxX)
                            maxX = pointInUcs.X;
                        if (pointInUcs.Y < minY)
                            minY = pointInUcs.Y;
                        if (pointInUcs.Y > maxY)
                            maxY = pointInUcs.Y;
                        if (pointInUcs.Z < minZ)
                            minZ = pointInUcs.Z;
                        if (pointInUcs.Z > maxZ)
                            maxZ = pointInUcs.Z;
                    }
                }
            });

        if (!hasBounds)
        {
            error = "无法计算所选对象的镜像中心。";
            return false;
        }

        var centerInUcs = new Point3d(
            (minX + maxX) * 0.5,
            (minY + maxY) * 0.5,
            (minZ + maxZ) * 0.5);
        centerPoint = centerInUcs.TransformBy(currentUcs);
        return true;
    }

    private static bool TryExecuteMirrorFlip(
        Document doc,
        ObjectId[] selectedIds,
        Point3d centerPoint,
        ReflectFlipMode mirrorMode,
        out ObjectId[] resultIds,
        out string error)
    {
        resultIds = Array.Empty<ObjectId>();
        error = string.Empty;

        var mirrTextState = CadSystemVariableService.Capture(SystemVariableNames.MirrText);
        try
        {
            AcAp.SetSystemVariable(SystemVariableNames.MirrText, 0);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 设置 MIRRTEXT 失败（无效操作）", ex);
            error = ex.Message;
            mirrTextState.TryRestoreAll();
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 设置 MIRRTEXT 失败（参数错误）", ex);
            error = ex.Message;
            mirrTextState.TryRestoreAll();
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 设置 MIRRTEXT 失败（CAD）", ex);
            error = ex.Message;
            mirrTextState.TryRestoreAll();
            return false;
        }

        try
        {
            var ucs = doc.Editor.CurrentUserCoordinateSystem.CoordinateSystem3d;
            var axisDirection = mirrorMode == ReflectFlipMode.LeftRight ? ucs.Yaxis : ucs.Xaxis;
            if (!TryExecuteMirrorReplace(doc.Database, doc.Editor, selectedIds, centerPoint, axisDirection, out resultIds, out error))
                return false;

            return resultIds.Length > 0;
        }
        finally
        {
            mirrTextState.TryRestoreAll();
        }
    }

    private static bool TryExecuteMirrorReplace(
        Database database,
        Editor ed,
        ObjectId[] sourceIds,
        Point3d basePoint,
        Vector3d axisDirection,
        out ObjectId[] resultIds,
        out string error)
    {
        resultIds = Array.Empty<ObjectId>();
        error = string.Empty;

        if (sourceIds.Length == 0)
        {
            error = "没有可参与镜像的对象。";
            return false;
        }

        var appendedIds = new List<ObjectId>();
        ObjectEventHandler handler = (_, e) =>
        {
            if (e.DBObject is not Entity entity)
                return;

            if (entity.ObjectId.IsNull)
                return;

            if (entity.OwnerId != database.CurrentSpaceId)
                return;

            if (entity is AttributeReference || entity is SequenceEnd)
                return;

            appendedIds.Add(entity.ObjectId);
        };

        database.ObjectAppended += handler;
        try
        {
            var axis = axisDirection;
            if (axis.Length <= 1e-9)
            {
                error = "镜像轴方向无效。";
                return false;
            }

            ed.Command(
                CommandNames.Mirror,
                SelectionSet.FromObjectIds(sourceIds),
                string.Empty,
                basePoint,
                basePoint + axis.GetNormal(),
                DeleteSourceYesKeyword);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行原生 MIRROR 失败（无效操作）", ex);
            error = ex.Message;
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行原生 MIRROR 失败（参数错误）", ex);
            error = ex.Message;
            return false;
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 执行原生 MIRROR 失败（CAD）", ex);
            error = ex.Message;
            return false;
        }
        finally
        {
            database.ObjectAppended -= handler;
        }

        resultIds = ResolveMirrorResultIds(database, sourceIds, appendedIds);
        if (resultIds.Length == 0)
        {
            error = "镜像后未获取到结果对象。";
            return false;
        }

        return true;
    }

    private static ObjectId[] ResolveMirrorResultIds(Database database, ObjectId[] sourceIds, IReadOnlyList<ObjectId> appendedIds)
    {
        var normalizedAppendedIds = NormalizeSelectionIds(appendedIds);
        if (normalizedAppendedIds.Length > 0)
            return normalizedAppendedIds;

        var survivors = new List<ObjectId>(sourceIds.Length);
        CadDatabaseScope.Read(
            database,
            (_, transaction) =>
            {
                for (var index = 0; index < sourceIds.Length; index++)
                {
                    var objectId = sourceIds[index];
                    if (objectId.IsNull)
                        continue;

                    try
                    {
                        if (!CadDatabaseScope.TryOpenAs<Entity>(transaction, objectId, OpenMode.ForRead, out var entity) ||
                            entity == null ||
                            entity.IsErased)
                        {
                            continue;
                        }

                        if (entity.OwnerId != database.CurrentSpaceId)
                            continue;

                        if (entity is AttributeReference || entity is SequenceEnd)
                            continue;

                        survivors.Add(objectId);
                    }
                    catch
                    {
                        // 跳过已被原生命令替换掉的对象。
                    }
                }
            });

        return NormalizeSelectionIds(survivors);
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
        var seen = new HashSet<ObjectId>();
        var ids = new List<ObjectId>();

        foreach (var objectId in rawIds)
        {
            if (objectId.IsNull || !seen.Add(objectId))
                continue;

            ids.Add(objectId);
        }

        return ids.Count == 0 ? Array.Empty<ObjectId>() : ids.ToArray();
    }

    private static string NormalizeKeyword(string? keyword) => (keyword ?? string.Empty).Trim();

    private enum ReflectFlipMode
    {
        LeftRight,
        UpDown
    }
}
