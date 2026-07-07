using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;

namespace QlPlugin;

/// <summary>
/// 从当前 DWG 文档收集所有图块信息
/// </summary>
public static class BlockInfoCollector
{
    /// <summary>
    /// 从选中对象中收集图块，只返回选中对象涉及的图块
    /// </summary>
    public static List<BlockInfo> CollectBlocksFromSelection(ObjectId[] selectionIds)
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (doc == null || selectionIds.Length == 0) return [];

        var db = doc.Database;
        var blockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using (var tr = db.TransactionManager.StartTransaction())
        {
            try
            {
                foreach (var id in selectionIds)
                {
                    if (!id.IsValid || id.IsErased) continue;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent != null)
                        CollectBlockNamesFromEntity(ent, tr, blockNames);
                }
                tr.Commit();
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        if (blockNames.Count == 0) return [];

        return CollectBlocks().Where(b => blockNames.Contains(b.Name)).ToList();
    }

    /// <summary>
    /// 递归收集实体及其子实体中的图块名称
    /// </summary>
    private static void CollectBlockNamesFromEntity(Entity ent, Transaction tr, HashSet<string> blockNames)
    {
        if (ent is BlockReference br)
        {
            var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            blockNames.Add(btr.Name);

            foreach (ObjectId id in btr)
            {
                if (!id.IsValid || id.IsErased) continue;
                var subEnt = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (subEnt != null)
                    CollectBlockNamesFromEntity(subEnt, tr, blockNames);
            }
        }
    }

    /// <summary>
    /// 收集当前文档中所有图块，按大小排序
    /// </summary>
    public static List<BlockInfo> CollectBlocks()
    {
        var result = new List<BlockInfo>();
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (doc == null) return result;

        var db = doc.Database;
        using var tr = db.TransactionManager.StartTransaction();

        try
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            foreach (ObjectId blockId in blockTable)
            {
                var blockRecord = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);

                // 跳过 *Model_Space 和 *Paper_Space 等特殊块
                if (blockRecord.IsLayout || blockRecord.IsFromExternalReference)
                    continue;

                var info = new BlockInfo
                {
                    Name = blockRecord.Name,
                    EntityCount = CountEntities(blockRecord, tr),
                    ReferenceCount = GetReferenceCount(blockRecord, tr),
                    IsAnonymous = blockRecord.IsAnonymous
                };

                result.Add(info);
            }

            tr.Commit();
        }
        catch (System.Exception)
        {
            tr.Abort();
            throw;
        }

        // 按大小排序：实体数量 * 引用数量，大的在前
        return result.OrderByDescending(b => b.EstimatedSize).ToList();
    }

    /// <summary>
    /// 统计图块内的实体数量
    /// </summary>
    private static int CountEntities(BlockTableRecord blockRecord, Transaction tr)
    {
        int count = 0;
        foreach (ObjectId id in blockRecord)
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// 获取图块引用数量（包含匿名块）
    /// </summary>
    private static int GetReferenceCount(BlockTableRecord blockRecord, Transaction tr)
    {
        try
        {
            var refIds = blockRecord.GetBlockReferenceIds(true, true);
            return refIds.Count;
        }
        catch
        {
            return 0;
        }
    }
}
