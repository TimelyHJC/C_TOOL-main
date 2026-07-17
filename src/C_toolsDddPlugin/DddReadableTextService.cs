using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;

namespace C_toolsDddPlugin;

internal static class DddReadableTextService
{
    internal static bool TryReadTextFromId(Document doc, ObjectId id, out string text)
    {
        text = "";
        if (id.IsNull || !id.IsValid)
            return false;

        using var tr = doc.TransactionManager.StartTransaction();
        var ok = TryReadTextFromObjectId(tr, id, out text);
        tr.Commit();
        return ok;
    }

    internal static bool TryReadTextFromIds(Document doc, IReadOnlyList<ObjectId> ids, out string text)
    {
        text = "";
        var parts = new List<string>();
        var visitedBlocks = new HashSet<ObjectId>();

        using (var tr = doc.TransactionManager.StartTransaction())
        {
            foreach (var id in ids)
            {
                if (!id.IsValid)
                    continue;

                try
                {
                    if (TryReadTextFromObjectId(tr, id, out var value, visitedBlocks))
                        AddTextPart(parts, value);
                }
                catch (AcRx.Exception)
                {
                }
            }

            tr.Commit();
        }

        if (parts.Count == 0)
            return false;

        text = string.Join(Environment.NewLine, parts);
        return DddTextContentHelper.HasVisibleText(text);
    }

    private static bool TryReadTextFromObjectId(Transaction tr, ObjectId id, out string text)
    {
        return TryReadTextFromObjectId(tr, id, out text, new HashSet<ObjectId>());
    }

    private static bool TryReadTextFromObjectId(
        Transaction tr,
        ObjectId id,
        out string text,
        HashSet<ObjectId> visitedBlocks)
    {
        text = "";
        if (id.IsNull || !id.IsValid)
            return false;

        var dbObject = tr.GetObject(id, OpenMode.ForRead, false);
        return TryReadDisplayText(dbObject, tr, out text, visitedBlocks);
    }

    private static bool TryReadDisplayText(
        DBObject dbObject,
        Transaction tr,
        out string text,
        HashSet<ObjectId> visitedBlocks)
    {
        text = "";
        if (dbObject is Entity entity && !entity.Visible)
            return false;

        switch (dbObject)
        {
            case AttributeReference attribute:
                text = ReadAttributeText(attribute);
                return DddTextContentHelper.HasVisibleText(text);
            case DBText dbText:
                text = DddTextContentHelper.ToEditableText(dbText.TextString, isMText: false);
                return DddTextContentHelper.HasVisibleText(text);
            case MText mText:
                text = DddTextContentHelper.ToEditableText(mText);
                return DddTextContentHelper.HasVisibleText(text);
            case Dimension dimension:
                return TryReadDimensionDisplayText(dimension, out text);
            case MLeader leader:
                return TryReadMLeaderText(leader, out text);
            case BlockReference blockReference:
                return TryReadBlockReferenceText(blockReference, tr, out text, visitedBlocks);
            default:
                return false;
        }
    }

    private static string ReadAttributeText(AttributeReference attribute)
    {
        if (!attribute.IsMTextAttribute)
            return DddTextContentHelper.ToEditableText(attribute.TextString, isMText: false);

        var mText = attribute.MTextAttribute;
        if (mText != null)
        {
            var text = DddTextContentHelper.ToEditableText(mText);
            if (DddTextContentHelper.HasVisibleText(text))
                return text;
        }

        return DddTextContentHelper.ToEditableText(attribute.TextString, isMText: false);
    }

    private static bool TryReadMLeaderText(MLeader leader, out string text)
    {
        text = "";
        try
        {
            var mText = leader.MText;
            if (mText == null)
                return false;

            text = DddTextContentHelper.ToEditableText(mText);
            return DddTextContentHelper.HasVisibleText(text);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取多重引线文字失败（无效操作）", ex);
            return false;
        }
        catch (AcRx.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取多重引线文字失败（CAD）", ex);
            return false;
        }
    }

    private static bool TryReadDimensionDisplayText(Dimension dimension, out string text)
    {
        text = "";
        if (TryExplodeAnnotationText(dimension, out text))
            return true;

        text = DddTextContentHelper.ToEditableText(dimension.DimensionText, isMText: false);
        if (DddTextContentHelper.HasVisibleText(text))
            return true;

        try
        {
            text = dimension.Measurement.ToString("0.########", CultureInfo.InvariantCulture);
            return DddTextContentHelper.HasVisibleText(text);
        }
        catch (AcRx.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取标注测量值失败（CAD）", ex);
            text = "";
            return false;
        }
    }

    private static bool TryReadBlockReferenceText(
        BlockReference blockReference,
        Transaction tr,
        out string text,
        HashSet<ObjectId> visitedBlocks)
    {
        text = "";
        var parts = new List<string>();

        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            try
            {
                if (tr.GetObject(attributeId, OpenMode.ForRead, false) is AttributeReference attribute)
                    AddTextPart(parts, ReadAttributeText(attribute));
            }
            catch (AcRx.Exception)
            {
            }
        }

        if (!blockReference.BlockTableRecord.IsNull && visitedBlocks.Add(blockReference.BlockTableRecord))
        {
            try
            {
                if (tr.GetObject(blockReference.BlockTableRecord, OpenMode.ForRead, false) is BlockTableRecord blockRecord)
                {
                    foreach (ObjectId childId in blockRecord)
                    {
                        try
                        {
                            if (TryReadTextFromObjectId(tr, childId, out var childText, visitedBlocks))
                                AddTextPart(parts, childText);
                        }
                        catch (AcRx.Exception)
                        {
                        }
                    }
                }
            }
            catch (AcRx.Exception)
            {
            }

            visitedBlocks.Remove(blockReference.BlockTableRecord);
        }

        if (parts.Count == 0)
            return false;

        text = string.Join(Environment.NewLine, parts);
        return DddTextContentHelper.HasVisibleText(text);
    }

    private static bool TryExplodeAnnotationText(Entity entity, out string text)
    {
        text = "";
        var parts = new List<string>();
        using var objects = new DBObjectCollection();

        try
        {
            entity.Explode(objects);
            foreach (DBObject obj in objects)
            {
                try
                {
                    switch (obj)
                    {
                        case DBText dbText:
                            AddTextPart(parts, DddTextContentHelper.ToEditableText(dbText.TextString, isMText: false));
                            break;
                        case MText mText:
                            AddTextPart(parts, DddTextContentHelper.ToEditableText(mText));
                            break;
                    }
                }
                finally
                {
                    obj.Dispose();
                }
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("分解标注读取文字失败（无效操作）", ex);
        }
        catch (AcRx.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("分解标注读取文字失败（CAD）", ex);
        }

        if (parts.Count == 0)
            return false;

        text = string.Join(Environment.NewLine, parts);
        return DddTextContentHelper.HasVisibleText(text);
    }

    private static void AddTextPart(List<string> parts, string? text)
    {
        var normalized = DddTextContentHelper.NormalizeLineEndings(text).Trim();
        if (DddTextContentHelper.HasVisibleText(normalized))
            parts.Add(normalized);
    }
}
