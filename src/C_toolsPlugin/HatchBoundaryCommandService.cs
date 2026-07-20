using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsPlugin;

/// <summary>
/// F_HB：根据所选填充图案调用 AutoCAD 原生命令生成边界线。
/// </summary>
internal static class HatchBoundaryCommandService
{
    internal static void Run()
    {
        CadCommandContext.ExecuteInActiveDocument("执行 F_HB", (doc, ed) =>
        {
            if (!TryGetHatchSelection(ed, out var selectionSet, out var selectedCount, out var cancelled))
            {
                ed.WriteMessage(cancelled
                    ? "\nC_TOOL：F_HB 已取消。"
                    : "\nC_TOOL：F_HB 未选择任何填充对象。");
                return;
            }

            try
            {
                ed.Command(
                    CommandNames.HatchGenerateBoundary,
                    selectionSet!,
                    string.Empty);

                ed.WriteMessage($"\nC_TOOL：F_HB 已根据 {selectedCount} 个填充对象生成边界线。");
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_HB 调用原生 HATCHGENERATEBOUNDARY 失败（无效操作）", ex);
                ed.WriteMessage($"\nC_TOOL：F_HB 失败：{ex.Message}");
            }
            catch (ArgumentException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_HB 调用原生 HATCHGENERATEBOUNDARY 失败（参数错误）", ex);
                ed.WriteMessage($"\nC_TOOL：F_HB 失败：{ex.Message}");
            }
            catch (AcadRuntimeException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_HB 调用原生 HATCHGENERATEBOUNDARY 失败（CAD）", ex);
                ed.WriteMessage($"\nC_TOOL：F_HB 失败：{ex.Message}");
            }
        });
    }

    private static bool TryGetHatchSelection(
        Editor ed,
        out SelectionSet? selectionSet,
        out int selectedCount,
        out bool cancelled)
    {
        selectionSet = null;
        selectedCount = 0;
        cancelled = false;

        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null)
        {
            var hatchIds = FilterHatchIds(implied.Value);
            if (hatchIds.Length > 0)
            {
                ed.SetImpliedSelection(Array.Empty<ObjectId>());
                selectionSet = SelectionSet.FromObjectIds(hatchIds);
                selectedCount = hatchIds.Length;
                return true;
            }
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = "\nC_TOOL：选择要生成边界线的填充对象："
        };
        var filter = new SelectionFilter(new[]
        {
            new TypedValue((int)DxfCode.Start, "HATCH")
        });
        var result = ed.GetSelection(options, filter);
        if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
        {
            cancelled = result.Status == PromptStatus.Cancel;
            return false;
        }

        selectionSet = result.Value;
        selectedCount = result.Value.Count;
        return true;
    }

    private static ObjectId[] FilterHatchIds(SelectionSet selectionSet)
    {
        var ids = new List<ObjectId>(selectionSet.Count);
        foreach (SelectedObject selectedObject in selectionSet)
        {
            if (selectedObject?.ObjectId.IsNull != false)
                continue;

            var objectClass = selectedObject.ObjectId.ObjectClass;
            if (!string.Equals(objectClass?.DxfName, "HATCH", StringComparison.OrdinalIgnoreCase))
                continue;

            ids.Add(selectedObject.ObjectId);
        }

        if (ids.Count == 0)
            return Array.Empty<ObjectId>();

        return ids.ToArray();
    }
}
