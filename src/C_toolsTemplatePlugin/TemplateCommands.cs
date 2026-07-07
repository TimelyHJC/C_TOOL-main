using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace C_toolsTemplatePlugin;

/// <summary>
/// 模板命令集，演示只读查询与写库命令的标准写法。
/// </summary>
public sealed class TemplateCommands
{
    [CommandMethod(TemplatePluginCommandIds.CommandGroup, TemplatePluginCommandIds.Hello, CommandFlags.Modal)]
    public void ShowHello()
    {
        if (!TemplateCommandExecutor.TryGetActiveDocument("模板命令自检", out _, out var editor) ||
            editor == null)
        {
            return;
        }

        editor.WriteMessage(
            "\nC_toolsTemplatePlugin 已加载。可继续测试 CTPLCOUNTLINES 和 CTPLADDCIRCLE。");
    }

    [CommandMethod(TemplatePluginCommandIds.CommandGroup, TemplatePluginCommandIds.CountLines, CommandFlags.Modal)]
    public void CountSelectedLines()
    {
        TemplateCommandExecutor.ExecuteRead("统计直线长度", (document, database, editor, transaction) =>
        {
            var selectionOptions = new PromptSelectionOptions
            {
                MessageForAdding = "\n选择需要统计的直线对象: ",
                AllowDuplicates = false
            };

            var selectionFilter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LINE")
            });

            var selectionResult = editor.GetSelection(selectionOptions, selectionFilter);
            if (selectionResult.Status != PromptStatus.OK || selectionResult.Value == null)
            {
                editor.WriteMessage("\n未选择任何直线。");
                return;
            }

            var objectIds = selectionResult.Value.GetObjectIds();
            double totalLength = 0d;

            foreach (var objectId in objectIds)
            {
                var line = TemplateCommandExecutor.OpenAs<Line>(transaction, objectId, OpenMode.ForRead);
                totalLength += line.Length;
            }

            editor.WriteMessage(
                $"\n共选择 {objectIds.Length} 条直线，总长度 = {totalLength:0.###}。");
        });
    }

    [CommandMethod(TemplatePluginCommandIds.CommandGroup, TemplatePluginCommandIds.AddCircle, CommandFlags.Modal)]
    public void AddCircleToModelSpace()
    {
        TemplateCommandExecutor.ExecuteWrite("创建示例圆", (document, database, editor, transaction) =>
        {
            var centerResult = editor.GetPoint(new PromptPointOptions("\n指定圆心: "));
            if (centerResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n命令已取消。");
                return;
            }

            var radiusOptions = new PromptDoubleOptions("\n指定半径: ")
            {
                AllowNegative = false,
                AllowZero = false,
                AllowNone = false,
                DefaultValue = 1000d,
                UseDefaultValue = true
            };

            var radiusResult = editor.GetDouble(radiusOptions);
            if (radiusResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\n命令已取消。");
                return;
            }

            var modelSpace = TemplateCommandExecutor.OpenModelSpaceForWrite(database, transaction);

            using var circle = new Circle(centerResult.Value, Vector3d.ZAxis, radiusResult.Value);
            modelSpace.AppendEntity(circle);
            transaction.AddNewlyCreatedDBObject(circle, true);

            editor.WriteMessage(
                $"\n已在模型空间创建圆，半径 = {radiusResult.Value:0.###}。");
        });
    }
}
