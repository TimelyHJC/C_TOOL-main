using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using C_toolsShared;

namespace C_toolsPlugin;

internal static class LayfrzFallbackService
{
    internal static void Run()
    {
        CadCommandContext.ExecuteInActiveDocument("LAYFRZ", (doc, ed) =>
        {
            var implied = ed.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value is { Count: > 0 })
            {
                ed.SetImpliedSelection(Array.Empty<ObjectId>());
                var result = FreezeLayersByEntityIds(doc, implied.Value.GetObjectIds());
                WriteResult(ed, result);
                return;
            }

            var anyPicked = false;
            while (true)
            {
                var options = new PromptEntityOptions("\n选择要冻结其图层的对象，或按 Enter 结束: ")
                {
                    AllowNone = true
                };

                var picked = ed.GetEntity(options);
                if (picked.Status == PromptStatus.None)
                    break;
                if (picked.Status == PromptStatus.Cancel)
                {
                    if (!anyPicked)
                        ed.WriteMessage("\nLAYFRZ 已取消。");
                    break;
                }
                if (picked.Status != PromptStatus.OK)
                    break;

                anyPicked = true;
                var result = FreezeLayersByEntityIds(doc, new[] { picked.ObjectId });
                WriteResult(ed, result);
            }
        });
    }

    private static FreezeLayerResult FreezeLayersByEntityIds(Document doc, IEnumerable<ObjectId> entityIds)
    {
        var uniqueLayerIds = new HashSet<ObjectId>();
        var readSkipped = 0;

        CadDatabaseScope.Read(doc, (_, tr) =>
        {
            foreach (var entityId in entityIds)
            {
                if (entityId.IsNull)
                {
                    readSkipped++;
                    continue;
                }

                if (!CadDatabaseScope.TryOpenAs<Entity>(tr, entityId, OpenMode.ForRead, out var entity) ||
                    entity == null ||
                    entity.LayerId.IsNull)
                {
                    readSkipped++;
                    continue;
                }

                uniqueLayerIds.Add(entity.LayerId);
            }
        });

        if (uniqueLayerIds.Count == 0)
            return new FreezeLayerResult(0, 0, 0, readSkipped, Array.Empty<string>());

        return CadDatabaseScope.Write(doc, (db, tr) =>
        {
            var frozen = 0;
            var alreadyFrozen = 0;
            var currentSkipped = 0;
            var skipped = readSkipped;
            var names = new List<string>();
            var currentLayerId = db.Clayer;

            foreach (var layerId in uniqueLayerIds)
            {
                if (layerId.IsNull)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    var layer = CadDatabaseScope.OpenAs<LayerTableRecord>(tr, layerId, OpenMode.ForRead);
                    var layerName = (layer.Name ?? "").Trim();

                    if (layerId == currentLayerId)
                    {
                        currentSkipped++;
                        continue;
                    }

                    if (layer.IsFrozen)
                    {
                        alreadyFrozen++;
                        continue;
                    }

                    if (!layer.IsWriteEnabled)
                        layer.UpgradeOpen();
                    layer.IsFrozen = true;
                    frozen++;
                    if (layerName.Length > 0)
                        names.Add(layerName);
                }
                catch (InvalidOperationException ex)
                {
                    skipped++;
                    C_toolsDiagnostics.LogNonFatal("LAYFRZ fallback failed to freeze layer (invalid operation)", ex);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    skipped++;
                    C_toolsDiagnostics.LogNonFatal("LAYFRZ fallback failed to freeze layer (CAD)", ex);
                }
            }

            return new FreezeLayerResult(frozen, alreadyFrozen, currentSkipped, skipped, names);
        }, requireDocumentLock: true);
    }

    private static void WriteResult(Editor ed, FreezeLayerResult result)
    {
        if (result.FrozenCount > 0)
        {
            var layerText = result.FrozenLayerNames.Count > 0
                ? "：" + string.Join("、", result.FrozenLayerNames)
                : "";
            ed.WriteMessage($"\n已冻结 {result.FrozenCount} 个图层{layerText}。");
            return;
        }

        if (result.CurrentLayerSkippedCount > 0)
        {
            ed.WriteMessage("\n不能冻结当前图层。");
            return;
        }

        if (result.AlreadyFrozenCount > 0)
        {
            ed.WriteMessage("\n所选对象所在图层已冻结。");
            return;
        }

        ed.WriteMessage(result.SkippedCount > 0
            ? "\n未能冻结所选对象所在图层。"
            : "\n未找到可冻结的图层。");
    }

    private sealed record FreezeLayerResult(
        int FrozenCount,
        int AlreadyFrozenCount,
        int CurrentLayerSkippedCount,
        int SkippedCount,
        IReadOnlyList<string> FrozenLayerNames);
}
