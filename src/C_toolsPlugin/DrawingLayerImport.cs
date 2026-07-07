using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsPlugin;

/// <summary>从当前活动文档的图层表生成「图层命令」行（仅填图层名与样式；不默认带入原生图层说明；图层快捷键由用户自行填写）。</summary>
internal static class DrawingLayerImport
{
    internal static List<CommandCatalogRow> BuildLayerShortcutRowsFromCurrentDrawing(Document doc)
    {
        var rows = CadDatabaseScope.Read(
            doc,
            (db, tr) =>
            {
                var result = new List<CommandCatalogRow>();
                var layerTable = CadDatabaseScope.OpenAs<LayerTable>(tr, db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in layerTable)
                {
                    if (!CadDatabaseScope.TryOpenAs<LayerTableRecord>(tr, id, OpenMode.ForRead, out var layerRecord) ||
                        layerRecord == null)
                    {
                        continue;
                    }

                    var layerName = layerRecord.Name ?? "";
                    if (layerName.Length == 0)
                        continue;

                    var colorStr = "";
                    try
                    {
                        if (layerRecord.Color.ColorMethod == ColorMethod.ByAci)
                            colorStr = layerRecord.Color.ColorIndex.ToString();
                    }
                    catch
                    {
                    }

                    var linetypeName = "";
                    try
                    {
                        if (!layerRecord.LinetypeObjectId.IsNull &&
                            CadDatabaseScope.TryOpenAs<LinetypeTableRecord>(tr, layerRecord.LinetypeObjectId, OpenMode.ForRead, out var linetypeRecord) &&
                            linetypeRecord != null)
                        {
                            linetypeName = linetypeRecord.Name ?? "";
                        }
                    }
                    catch
                    {
                    }

                    var lineWeightText = "";
                    try
                    {
                        var lineWeight = layerRecord.LineWeight;
                        if (lineWeight != LineWeight.ByLineWeightDefault &&
                            lineWeight != LineWeight.ByLayer &&
                            lineWeight != LineWeight.ByBlock)
                        {
                            lineWeightText = lineWeight.ToString();
                        }
                    }
                    catch
                    {
                    }

                    result.Add(new CommandCatalogRow(PluginCommandIds.LayerShortcutCatalogCommandLabel, "—", "当前图纸",
                        CadCommandCatalogBuilder.TagLayerShortcut)
                    {
                        Alias = "",
                        LayerName = layerName,
                        LayerColor = colorStr,
                        LayerLinetype = linetypeName,
                        LayerLineWeight = lineWeightText,
                        IsUserModified = true
                    });
                }

                return result;
            },
            requireDocumentLock: true);

        return rows.OrderBy(r => r.LayerName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
