using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace QlPlugin;

/// <summary>
/// QL 命令 - 显示图块列表并按大小排序。先选中对象再运行则只显示选中图块。
/// </summary>
public class QlCommand
{
    [CommandMethod("QL", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void ShowBlockList()
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog("请先打开一个 DWG 文件。");
            return;
        }

        try
        {
            List<BlockInfo> blocks;
            var ed = doc.Editor;
            var implied = ed.SelectImplied();

            var fromSelection = false;
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
            {
                blocks = BlockInfoCollector.CollectBlocksFromSelection(implied.Value.GetObjectIds());
                fromSelection = true;
            }
            else
            {
                blocks = BlockInfoCollector.CollectBlocks();
            }

            var form = new BlockListForm(blocks, fromSelection);
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);

            // 若用户双击图块名称插入，执行插入
            if (!string.IsNullOrEmpty(form.BlockNameToInsert))
            {
                var (success, msg) = BlockInserter.InsertBlock(form.BlockNameToInsert);
                if (!success)
                    Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog(msg);
            }
        }
        catch (System.Exception ex)
        {
            Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog($"加载图块列表时出错：{ex.Message}");
        }
    }

    [CommandMethod("QLL", CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void RunCleanup()
    {
        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
        {
            Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog("请先打开一个 DWG 文件。");
            return;
        }

        try
        {
            var ed = doc.Editor;
            var implied = ed.SelectImplied();
            var hasSelection = implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0;

            var result = DwgCleanup.RunFullCleanup(hasSelection);
            Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog("清理完成\n\n" + result);
        }
        catch (System.Exception ex)
        {
            Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog($"清理时出错：{ex.Message}");
        }
    }

    [CommandMethod("KKK", CommandFlags.Modal)]
    public void ShowCommandShortcuts()
    {
        try
        {
            var form = new CommandShortcutForm();
            Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);
        }
        catch (System.Exception ex)
        {
            Autodesk.AutoCAD.ApplicationServices.Application.ShowAlertDialog($"打开快捷命令页面时出错：{ex.Message}");
        }
    }
}
