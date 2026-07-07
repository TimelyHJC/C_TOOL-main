using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsShared;

namespace C_toolsPlugin;

/// <summary>
/// 与 Autodesk 文档「Make a Layer Current (.NET)」一致：事务内打开 <see cref="LayerTable"/>，设 <see cref="Database.Clayer"/>；
/// 有选集时写 <see cref="Entity.Layer"/>。
/// </summary>
internal static class LayerApplyService
{
    internal static void EnsureLayer(Transaction tr, Database db, LayerShortcutEntry entry)
    {
        var layerName = entry.LayerName.Trim();
        var lt = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(layerName))
        {
            var ltr = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, lt[layerName], OpenMode.ForRead);
            var wantedDescription = LayerStyleHelper.NormalizeDescription(entry.Description);
            if (!string.Equals((ltr.Description ?? "").Trim(), wantedDescription, StringComparison.Ordinal))
            {
                ltr.UpgradeOpen();
                LayerStyleHelper.ApplyDescription(ltr, entry);
            }
            return;
        }

        lt.UpgradeOpen();
        var newLayerRecord = new LayerTableRecord { Name = layerName };
        LayerStyleHelper.ApplyToNewLayer(tr, db, newLayerRecord, entry);
        lt.Add(newLayerRecord);
        tr.AddNewlyCreatedDBObject(newLayerRecord, true);
    }

    internal static void SetCurrentLayerOnly(Document doc, LayerShortcutEntry entry)
    {
        var db = doc.Database;
        var layerName = entry.LayerName.Trim();
        if (layerName.Length == 0)
            return;

        CadDatabaseScope.Write(doc, (_, tr) =>
        {
            EnsureLayer(tr, db, entry);
            var lt = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
            db.Clayer = lt[layerName];
        }, requireDocumentLock: true);
    }

    /// <summary>
    /// 预选集存在时：确保目标层存在 → 设 <see cref="Database.Clayer"/> → 逐个把图元 <see cref="Entity.Layer"/> 改为目标层。
    /// 与 Autodesk「Make a Layer Current」及图元改层（如 <see href="https://www.keanw.com/2007/08/moving-entities.html"/>）一致。
    /// </summary>
    /// <returns>(成功改层的实体数, 跳过数：锁定层、无法写打开等)。</returns>
    internal static (int Changed, int Skipped) ApplyToSelection(Document doc, SelectionSet ss, LayerShortcutEntry entry)
    {
        var db = doc.Database;
        var layerName = entry.LayerName.Trim();

        return CadDatabaseScope.Write(doc, (_, tr) =>
        {
            var changed = 0;
            var skipped = 0;
            EnsureLayer(tr, db, entry);
            var lt = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
            db.Clayer = lt[layerName];

            foreach (SelectedObject? so in ss)
            {
                if (so == null)
                    continue;
                try
                {
                    if (!CadDatabaseScope.TryOpenAs<Entity>(tr, so.ObjectId, OpenMode.ForWrite, out var ent) ||
                        ent == null)
                    {
                        skipped++;
                        continue;
                    }

                    ent.Layer = layerName;
                    if (entry.ColorIndex is >= 1 and <= 255)
                        ent.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)entry.ColorIndex.Value);

                    changed++;
                }
                catch (InvalidOperationException ex)
                {
                    skipped++;
                    C_toolsDiagnostics.LogNonFatal("改层时无效操作（可能实体已删除）", ex);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    skipped++;
                    C_toolsDiagnostics.LogNonFatal($"改层时CAD异常: {ex.Message}", ex);
                }
            }

            return (changed, skipped);
        }, requireDocumentLock: true);
    }

    internal static void SyncDescriptionsToCurrentDrawing(Document doc, IReadOnlyList<LayerShortcutEntry> entries)
    {
        if (entries.Count == 0)
            return;

        CadDatabaseScope.Write(doc, (db, tr) =>
        {
            var lt = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
            var descriptionsByLayer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var layerName = (entry.LayerName ?? "").Trim();
                if (layerName.Length == 0)
                    continue;

                descriptionsByLayer[layerName] = LayerStyleHelper.NormalizeDescription(entry.Description);
            }

            foreach (var pair in descriptionsByLayer)
            {
                if (!lt.Has(pair.Key))
                    continue;

                var ltr = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, lt[pair.Key], OpenMode.ForRead);
                if (string.Equals((ltr.Description ?? "").Trim(), pair.Value, StringComparison.Ordinal))
                    continue;

                ltr.UpgradeOpen();
                ltr.Description = pair.Value;
            }
        }, requireDocumentLock: true);
    }
}
