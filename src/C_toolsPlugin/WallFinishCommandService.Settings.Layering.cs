using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static partial class SettingsManager
    {
        internal static LayerShortcutEntry? TryFindFinishLayerEntry(IReadOnlyList<LayerShortcutEntry> layerEntries)
        {
            LayerShortcutEntry? matchedEntry = null;
            for (var i = 0; i < layerEntries.Count; i++)
            {
                var entry = layerEntries[i];
                if (string.IsNullOrWhiteSpace(entry.LayerName))
                    continue;

                var description = (entry.Description ?? "").Trim();
                if (!string.Equals(description, FinishLayerDescription, StringComparison.Ordinal))
                    continue;

                if (matchedEntry != null)
                    return null;

                matchedEntry = entry;
            }

            return matchedEntry;
        }

        internal static LayerShortcutEntry? TryFindFinishHatchEntry(IReadOnlyList<LayerShortcutEntry> layerEntries)
        {
            LayerShortcutEntry? matchedEntry = null;
            for (var i = 0; i < layerEntries.Count; i++)
            {
                var entry = layerEntries[i];
                if (HatchStyleSnapshot.TryParseJson(entry.HatchStyle) == null)
                    continue;

                var description = (entry.Description ?? "").Trim();
                if (!string.Equals(description, FinishHatchDescription, StringComparison.Ordinal))
                    continue;

                if (matchedEntry != null)
                    return null;

                matchedEntry = entry;
            }

            return matchedEntry;
        }

        internal static string GetCurrentLayerName(Document doc)
        {
            return CadSystemVariableService.GetTrimmedStringOrDefault(SystemVariableNames.Clayer, "0");
        }

        internal static string GetEntityLayerName(Database db, ObjectId entityId)
        {
            try
            {
                return CadDatabaseScope.Read(
                    db,
                    (_, tr) =>
                    {
                        if (!CadDatabaseScope.TryOpenAs<Entity>(tr, entityId, OpenMode.ForRead, out var entity) ||
                            entity == null)
                        {
                            return "0";
                        }

                        var layerName = (entity.Layer ?? "").Trim();
                        return layerName.Length == 0 ? "0" : layerName;
                    });
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_WCC 读取源墙面线图层失败（无效操作）", ex);
                return "0";
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_WCC 读取源墙面线图层失败（CAD）", ex);
                return "0";
            }
        }

        internal static LayerShortcutEntry? FindLayerShortcutEntry(
            IReadOnlyList<LayerShortcutEntry> layerEntries,
            string layerName)
        {
            var trimmedLayerName = (layerName ?? "").Trim();
            if (trimmedLayerName.Length == 0)
                return null;

            for (var i = 0; i < layerEntries.Count; i++)
            {
                var entryLayerName = (layerEntries[i].LayerName ?? "").Trim();
                if (string.Equals(entryLayerName, trimmedLayerName, StringComparison.OrdinalIgnoreCase))
                    return layerEntries[i];
            }

            return null;
        }

        internal static IReadOnlyList<string> LoadTargetLayerOptions(
            Database db,
            IReadOnlyList<LayerShortcutEntry> layerEntries,
            string fallbackLayerName)
        {
            var names = new List<string>();
            for (var i = 0; i < layerEntries.Count; i++)
            {
                var name = (layerEntries[i].LayerName ?? "").Trim();
                if (name.Length > 0)
                    names.Add(name);
            }

            try
            {
                names.AddRange(
                    CadDatabaseScope.Read(
                        db,
                        (database, tr) =>
                        {
                            var layerNames = new List<string>();
                            var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, database.LayerTableId, OpenMode.ForRead);
                            foreach (ObjectId layerId in layerTable)
                            {
                                if (!CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead, out var layer) ||
                                    layer == null)
                                {
                                    continue;
                                }

                                var name = (layer.Name ?? "").Trim();
                                if (name.Length > 0)
                                    layerNames.Add(name);
                            }

                            return layerNames;
                        }));
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_WCC 读取图层下拉数据失败（无效操作）", ex);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_WCC 读取图层下拉数据失败（CAD）", ex);
            }

            var fallback = (fallbackLayerName ?? "").Trim();
            if (fallback.Length > 0)
                names.Add(fallback);

            if (names.Count == 0)
                names.Add("0");

            return names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal static ObjectId EnsureLayer(
            Transaction tr,
            Database db,
            string layerName,
            LayerShortcutEntry? layerEntry,
            int? layerColorIndex)
        {
            var trimmedLayerName = (layerName ?? "").Trim();
            if (trimmedLayerName.Length == 0)
                trimmedLayerName = "0";

            var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(trimmedLayerName))
            {
                var layerId = layerTable[trimmedLayerName];
                ApplyLayerColorOverride(tr, layerId, layerColorIndex);
                return layerId;
            }

            layerTable.UpgradeOpen();
            var layerRecord = new LayerTableRecord
            {
                Name = trimmedLayerName
            };

            if (layerEntry != null)
                LayerStyleHelper.ApplyToNewLayer(tr, db, layerRecord, layerEntry);

            ApplyLayerColorOverride(layerRecord, layerColorIndex ?? layerEntry?.ColorIndex);
            layerTable.Add(layerRecord);
            tr.AddNewlyCreatedDBObject(layerRecord, true);
            return layerRecord.ObjectId;
        }

        internal static void ApplyLayerColorOverride(Transaction tr, ObjectId layerId, int? layerColorIndex)
        {
            if (layerColorIndex is not >= 1 or > 255)
                return;

            if (!CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead, out var layerRecord) ||
                layerRecord == null)
            {
                return;
            }

            if (!layerRecord.IsWriteEnabled)
                layerRecord.UpgradeOpen();

            ApplyLayerColorOverride(layerRecord, layerColorIndex);
        }

        internal static void ApplyLayerColorOverride(LayerTableRecord layerRecord, int? layerColorIndex)
        {
            if (layerColorIndex is not >= 1 or > 255)
                return;

            layerRecord.Color = Color.FromColorIndex(ColorMethod.ByAci, (short)layerColorIndex.Value);
        }
    }
}
