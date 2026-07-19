using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsDddPlugin;

internal static class DddTextMiddleAlignService
{
    private const string CommandName = C_toolsCommandIds.Ddd.TextMiddleAlign;
    private const double PointTolerance = 1e-6;

    internal static void Run(Document doc)
    {
        var editor = doc.Editor;
        var targetIds = GetPreselectedTargetIds(doc, out var hadPreselection);
        if (hadPreselection)
            editor.SetImpliedSelection(Array.Empty<ObjectId>());

        try
        {
            if (targetIds.Length == 0 &&
                !TryPromptTextSelection(doc, out targetIds))
            {
                return;
            }

            if (targetIds.Length == 0)
            {
                editor.WriteMessage($"\nC_TOOL：{CommandName} 未选择可修改的文字。");
                return;
            }

            var result = ApplyMiddleAlignment(doc, targetIds);
            if (result.ChangedCount > 0)
            {
                editor.WriteMessage($"\nC_TOOL：{CommandName} 已将 {result.ChangedCount} 个文字改为中间对齐。");
                return;
            }

            if (result.SupportedCount > 0)
            {
                editor.WriteMessage($"\nC_TOOL：{CommandName} 所选文字已是中间对齐。");
                return;
            }

            editor.WriteMessage($"\nC_TOOL：{CommandName} 所选对象中没有可修改的文字。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 修改文字对齐失败（无效操作）", ex);
            editor.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 修改文字对齐失败（CAD）", ex);
            editor.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{CommandName} 修改文字对齐失败", ex);
            editor.WriteMessage($"\nC_TOOL：{CommandName} 失败：{ex.Message}");
        }
    }

    private static bool TryPromptTextSelection(Document doc, out ObjectId[] targetIds)
    {
        targetIds = Array.Empty<ObjectId>();

        var options = new PromptSelectionOptions
        {
            AllowDuplicates = false,
            MessageForAdding = $"\nC_TOOL：{CommandName} 选择要改为中间对齐的文字：",
            MessageForRemoval = $"\nC_TOOL：{CommandName} 移除文字："
        };

        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "TEXT,MTEXT,ATTRIB")
        });

        var result = doc.Editor.GetSelection(options, filter);
        if (result.Status != PromptStatus.OK || result.Value == null)
            return false;

        targetIds = GetSupportedTextIds(doc, result.Value.GetObjectIds());
        if (targetIds.Length == 0)
            doc.Editor.WriteMessage($"\nC_TOOL：{CommandName} 未选择可修改的文字。");

        return targetIds.Length > 0;
    }

    private static ObjectId[] GetPreselectedTargetIds(Document doc, out bool hadPreselection)
    {
        hadPreselection = false;

        var result = doc.Editor.SelectImplied();
        if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
            return Array.Empty<ObjectId>();

        hadPreselection = true;
        return GetSupportedTextIds(doc, result.Value.GetObjectIds());
    }

    private static ObjectId[] GetSupportedTextIds(Document doc, IReadOnlyList<ObjectId> ids)
    {
        if (ids.Count == 0)
            return Array.Empty<ObjectId>();

        var textIds = new List<ObjectId>(ids.Count);
        var seen = new HashSet<ObjectId>();

        using var transaction = doc.TransactionManager.StartTransaction();
        foreach (var id in ids)
        {
            if (id.IsNull || !id.IsValid || !seen.Add(id))
                continue;

            try
            {
                var dbObject = transaction.GetObject(id, OpenMode.ForRead, false);
                if (IsSupportedTextObject(dbObject))
                    textIds.Add(id);
            }
            catch (AcadRuntimeException)
            {
            }
        }

        return textIds.Count == 0 ? Array.Empty<ObjectId>() : textIds.ToArray();
    }

    private static TextAlignmentResult ApplyMiddleAlignment(Document doc, IReadOnlyList<ObjectId> targetIds)
    {
        var result = new TextAlignmentResult();
        var seen = new HashSet<ObjectId>();

        using (doc.LockDocument())
        using (var transaction = doc.TransactionManager.StartTransaction())
        {
            foreach (var targetId in targetIds)
            {
                if (targetId.IsNull || !targetId.IsValid || !seen.Add(targetId))
                    continue;

                try
                {
                    var dbObject = transaction.GetObject(targetId, OpenMode.ForWrite, false);
                    if (!TryApplyMiddleAlignment(dbObject, doc.Database, out var changed))
                    {
                        result.SkippedCount++;
                        continue;
                    }

                    result.SupportedCount++;
                    if (changed)
                        result.ChangedCount++;
                }
                catch (AcadRuntimeException)
                {
                    result.SkippedCount++;
                }
            }

            if (result.ChangedCount > 0)
                transaction.Commit();
        }

        return result;
    }

    private static bool IsSupportedTextObject(DBObject dbObject)
    {
        return dbObject switch
        {
            AttributeReference => true,
            DBText => true,
            MText => true,
            _ => false
        };
    }

    private static bool TryApplyMiddleAlignment(DBObject dbObject, Database database, out bool changed)
    {
        changed = false;

        switch (dbObject)
        {
            case AttributeReference attribute:
                return TryApplyDbTextMiddleAlignment(attribute, database, out changed);
            case DBText dbText:
                return TryApplyDbTextMiddleAlignment(dbText, database, out changed);
            case MText mText:
                return TryApplyMTextMiddleAlignment(mText, out changed);
            default:
                return false;
        }
    }

    private static bool TryApplyDbTextMiddleAlignment(DBText text, Database database, out bool changed)
    {
        changed = text.Justify != AttachmentPoint.MiddleCenter;
        if (!changed)
            return true;

        var center = TryGetEntityCenter(text, out var geometricCenter)
            ? geometricCenter
            : ResolveDbTextAnchor(text);

        text.Justify = AttachmentPoint.MiddleCenter;
        text.AlignmentPoint = center;
        text.AdjustAlignment(database);
        TryRecordGraphicsModified(text);
        return true;
    }

    private static bool TryApplyMTextMiddleAlignment(MText text, out bool changed)
    {
        changed = text.Attachment != AttachmentPoint.MiddleCenter;
        if (!changed)
            return true;

        var center = TryGetEntityCenter(text, out var geometricCenter)
            ? geometricCenter
            : text.Location;

        text.Attachment = AttachmentPoint.MiddleCenter;
        text.Location = center;
        TryRecordGraphicsModified(text);
        return true;
    }

    private static Point3d ResolveDbTextAnchor(DBText text)
    {
        if (text.Justify != AttachmentPoint.BaseLeft &&
            !IsOrigin(text.AlignmentPoint))
        {
            return text.AlignmentPoint;
        }

        return text.Position;
    }

    private static bool TryGetEntityCenter(Entity entity, out Point3d center)
    {
        center = Point3d.Origin;

        try
        {
            var extents = entity.GeometricExtents;
            center = new Point3d(
                (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                (extents.MinPoint.Z + extents.MaxPoint.Z) * 0.5);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (AcadRuntimeException)
        {
            return false;
        }
    }

    private static bool IsOrigin(Point3d point)
    {
        return point.DistanceTo(Point3d.Origin) <= PointTolerance;
    }

    private static void TryRecordGraphicsModified(Entity entity)
    {
        try
        {
            entity.RecordGraphicsModified(true);
        }
        catch
        {
        }
    }

    private sealed class TextAlignmentResult
    {
        internal int SupportedCount { get; set; }
        internal int ChangedCount { get; set; }
        internal int SkippedCount { get; set; }
    }
}
