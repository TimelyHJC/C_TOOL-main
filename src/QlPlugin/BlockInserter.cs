using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace QlPlugin;

/// <summary>
/// 插入图块
/// </summary>
public static class BlockInserter
{
    /// <summary>
    /// 在用户指定位置插入图块
    /// </summary>
    public static (bool Success, string Message) InsertBlock(string blockName)
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return (false, "未打开文档");

        var db = doc.Database;
        var ed = doc.Editor;

        // 系统块不可插入
        if (blockName.StartsWith("*") || blockName.Equals("MODEL_SPACE", StringComparison.OrdinalIgnoreCase) ||
            blockName.Equals("PAPER_SPACE", StringComparison.OrdinalIgnoreCase))
            return (false, $"无法插入系统块: {blockName}");

        try
        {
            // 提示用户指定插入点
            var ppo = new PromptPointOptions("\n指定图块插入点: ")
            {
                AllowNone = false
            };
            var ppr = ed.GetPoint(ppo);
            if (ppr.Status != PromptStatus.OK)
                return (false, "已取消");

            var insertPoint = ppr.Value;

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
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                var blockRef = new BlockReference(insertPoint, blockId);
                space.AppendEntity(blockRef);
                tr.AddNewlyCreatedDBObject(blockRef, true);

                tr.Commit();
                return (true, $"已插入图块: {blockName}");
            }
            catch (System.Exception ex)
            {
                tr.Abort();
                return (false, ex.Message);
            }
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
