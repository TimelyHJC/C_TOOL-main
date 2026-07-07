using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace QlPlugin;

/// <summary>
/// 删除图块及其所有引用
/// </summary>
public static class BlockDeleter
{
    /// <summary>
    /// 删除指定名称的图块（包括所有引用和块定义）
    /// </summary>
    /// <returns>是否成功删除</returns>
    public static (bool Success, string Message) DeleteBlock(string blockName)
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return (false, "未打开文档");

        var db = doc.Database;
        var ed = doc.Editor;

        // 不可删除的系统块
        if (blockName.StartsWith("*") || blockName.Equals("MODEL_SPACE", StringComparison.OrdinalIgnoreCase) ||
            blockName.Equals("PAPER_SPACE", StringComparison.OrdinalIgnoreCase))
            return (false, $"无法删除系统块: {blockName}");

        using var tr = db.TransactionManager.StartTransaction();

        try
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);

            if (!blockTable.Has(blockName))
            {
                tr.Abort();
                return (false, $"图块不存在: {blockName}");
            }

            var blockId = blockTable[blockName];
            var blockRecord = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);

            if (blockRecord.IsLayout || blockRecord.IsFromExternalReference)
            {
                tr.Abort();
                return (false, $"无法删除: {blockName}（布局或外部参照）");
            }

            // 获取所有引用并删除
            var refIds = blockRecord.GetBlockReferenceIds(false, true);

            blockRecord.UpgradeOpen();

            foreach (ObjectId refId in refIds)
            {
                if (refId.IsValid && !refId.IsErased)
                {
                    var ent = tr.GetObject(refId, OpenMode.ForWrite) as Entity;
                    ent?.Erase();
                }
            }

            // 删除块定义
            blockRecord.Erase();
            tr.Commit();

            return (true, $"已删除图块: {blockName}");
        }
        catch (System.Exception ex)
        {
            tr.Abort();
            return (false, $"删除失败: {ex.Message}");
        }
    }
}
