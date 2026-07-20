using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsPlugin;

/// <summary>
/// F_WB / F_SB：通过 AutoCAD 原生 DRAWORDER 调整所选对象绘图次序。
/// </summary>
internal static class DrawOrderCommandService
{
    internal static void SendToBack()
    {
        Run(PluginCommandIds.DrawOrderBack, CommandNames.DrawOrderBackOption, "后置");
    }

    internal static void BringToFront(string commandName)
    {
        Run(commandName, CommandNames.DrawOrderFrontOption, "前置");
    }

    private static void Run(string commandName, string nativeOption, string actionLabel)
    {
        CadCommandContext.ExecuteInActiveDocument($"执行 {commandName}", (doc, ed) =>
        {
            if (!TryGetSelectionSet(ed, actionLabel, out var selectionSet, out var selectedCount, out var cancelled))
            {
                ed.WriteMessage(cancelled
                    ? $"\nC_TOOL：{commandName} 已取消。"
                    : $"\nC_TOOL：{commandName} 未选择任何对象。");
                return;
            }

            try
            {
                ed.Command(
                    CommandNames.DrawOrder,
                    selectionSet!,
                    string.Empty,
                    nativeOption);

                ed.WriteMessage($"\nC_TOOL：{commandName} 已{actionLabel} {selectedCount} 个对象的绘图次序。");
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal($"{commandName} 调用原生 DRAWORDER 失败（无效操作）", ex);
                ed.WriteMessage($"\nC_TOOL：{commandName} 失败：{ex.Message}");
            }
            catch (ArgumentException ex)
            {
                C_toolsDiagnostics.LogNonFatal($"{commandName} 调用原生 DRAWORDER 失败（参数错误）", ex);
                ed.WriteMessage($"\nC_TOOL：{commandName} 失败：{ex.Message}");
            }
            catch (AcadRuntimeException ex)
            {
                C_toolsDiagnostics.LogNonFatal($"{commandName} 调用原生 DRAWORDER 失败（CAD）", ex);
                ed.WriteMessage($"\nC_TOOL：{commandName} 失败：{ex.Message}");
            }
        });
    }

    private static bool TryGetSelectionSet(
        Editor ed,
        string actionLabel,
        out SelectionSet? selectionSet,
        out int selectedCount,
        out bool cancelled)
    {
        selectionSet = null;
        selectedCount = 0;
        cancelled = false;

        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
        {
            ed.SetImpliedSelection(Array.Empty<ObjectId>());
            selectionSet = implied.Value;
            selectedCount = implied.Value.Count;
            return true;
        }

        var options = new PromptSelectionOptions
        {
            MessageForAdding = $"\nC_TOOL：选择要{actionLabel}绘图次序的对象："
        };
        var result = ed.GetSelection(options);
        if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0)
        {
            cancelled = result.Status == PromptStatus.Cancel;
            return false;
        }

        selectionSet = result.Value;
        selectedCount = result.Value.Count;
        return true;
    }
}
