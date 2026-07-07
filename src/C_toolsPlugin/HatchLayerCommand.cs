using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

/// <summary>
/// 带填充样式的图层快捷键命令。
/// 从 USERS1-4 读取图层名和填充参数，再通过统一 Hatch 启动服务执行。
/// </summary>
public class HatchLayerCommand
{
    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.HatchLayer, CommandFlags.Modal)]
    public void Execute()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var request = HatchLayerBridgeProtocol.TryReadRequest();
        if (request == null)
        {
            doc.Editor.WriteMessage("\nC_TOOL：F_HatchLayer 需要 USERS1（图层名）。");
            return;
        }

        try
        {
            doc.Editor.WriteMessage(
                $"\nC_TOOL：读取系统变量 USERS1={request.LayerName}, USERS2={request.PatternName}, USERS3={request.Scale}, USERS4={request.AngleDegrees}");
            HatchLaunchService.Start(doc, HatchBridgeRequestMapper.ToLaunchRequest(request));
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_HatchLayer 执行失败", ex);
            doc.Editor.WriteMessage($"\nC_TOOL：启动填充失败: {ex.Message}");
        }
        finally
        {
            HatchLayerBridgeProtocol.ClearRequest();
        }
    }

    /// <summary>
    /// 兜底命令：在 HATCH 完成后，将最后创建的填充对象改到指定图层
    /// 注意：此命令现在由 LayerShortcutExecutor 通过事件自动调用，也可手动调用
    /// </summary>
    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.HatchFixLayer, CommandFlags.Modal)]
    public void FixLayerAfterHatch()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;

        try
        {
            var targetLayer = GetPendingHatchLayer(doc);
            if (string.IsNullOrEmpty(targetLayer))
            {
                ed.WriteMessage("\nC_TOOL：F_HatchFixLayer 没有待处理的图层。");
                return;
            }

            var fixedCount = FixLastHatchLayer(doc, targetLayer!);

            if (fixedCount > 0)
            {
                ed.WriteMessage($"\nC_TOOL：已将 {fixedCount} 个填充对象改到图层 [{targetLayer}]。");
            }
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_HatchFixLayer 失败", ex);
            ed.WriteMessage($"\nC_TOOL：修改填充图层失败: {ex.Message}");
        }
    }

    private static string? GetPendingHatchLayer(Document doc)
    {
        return HatchPendingLayerStore.GetPendingLayer(doc);
    }

    private static int FixLastHatchLayer(Document doc, string targetLayer)
    {
        var changedCount = CadDatabaseScope.Write(
            doc,
            (db, tr) =>
            {
                var blockTable = CadDatabaseScope.OpenAs<BlockTable>(tr, db.BlockTableId, OpenMode.ForRead);
                var modelSpace = CadDatabaseScope.OpenAs<BlockTableRecord>(
                    tr,
                    blockTable[BlockTableRecord.ModelSpace],
                    OpenMode.ForRead);

                var lastHatchId = ObjectId.Null;
                foreach (ObjectId id in modelSpace)
                {
                    if (!id.IsValid ||
                        !CadDatabaseScope.TryOpenAs<Hatch>(tr, id, OpenMode.ForRead, out var hatch) ||
                        hatch == null)
                    {
                        continue;
                    }

                    lastHatchId = id;
                }

                if (lastHatchId.IsNull ||
                    !CadDatabaseScope.TryOpenAs<Hatch>(tr, lastHatchId, OpenMode.ForWrite, out var lastHatch) ||
                    lastHatch == null)
                {
                    return 0;
                }

                lastHatch.Layer = targetLayer;
                return 1;
            },
            requireDocumentLock: true);

        HatchPendingLayerStore.ClearPendingLayer(doc);
        return changedCount;
    }
}

internal static class HatchPendingLayerStore
{
    private static readonly Dictionary<string, string> s_pendingLayers = new(StringComparer.OrdinalIgnoreCase);

    internal static void SetPendingLayer(Document doc, string layerName)
    {
        var key = GetKey(doc);
        if (key.Length == 0)
            return;
        s_pendingLayers[key] = layerName;
    }

    internal static string? GetPendingLayer(Document doc)
    {
        var key = GetKey(doc);
        if (key.Length == 0)
            return null;
        return s_pendingLayers.TryGetValue(key, out var layer) ? layer : null;
    }

    internal static void ClearPendingLayer(Document doc)
    {
        var key = GetKey(doc);
        if (key.Length == 0)
            return;
        s_pendingLayers.Remove(key);
    }

    private static string GetKey(Document doc)
    {
        try
        {
            return doc.Database.FingerprintGuid.ToString();
        }
        catch
        {
            return doc.Name ?? string.Empty;
        }
    }
}

internal static class HatchLayerCompat
{
    internal static void RememberPendingLayer(Document doc, string layerName)
    {
        if (string.IsNullOrWhiteSpace(layerName))
            return;
        HatchPendingLayerStore.SetPendingLayer(doc, layerName.Trim());
    }
}

internal static class HatchLaunchServiceExtensions
{
    internal static void StartAndRememberPendingLayer(Document doc, HatchLaunchRequest request)
    {
        HatchLayerCompat.RememberPendingLayer(doc, request.LayerName);
        HatchLaunchService.Start(doc, request);
    }
}


