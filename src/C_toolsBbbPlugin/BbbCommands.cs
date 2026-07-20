using System.Windows;
using Autodesk.AutoCAD.Runtime;

namespace C_toolsBbbPlugin;

public class BbbCommands
{
    private static readonly ModelessWindowHost<BbbPanelWindow> s_hiddenDevicePanelHost = new();

    [CommandMethod(BbbPluginCommandIds.CommandGroup, BbbPluginCommandIds.Bbb, CommandFlags.Modal | CommandFlags.UsePickSet)]
    [CommandMethod(BbbPluginCommandIds.CommandGroup, C_toolsCommandIds.Bbb.AliasShort, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void ToggleBbbPanel()
    {
        OpenPanel();
    }

    private static void OpenPanel()
    {
        CadCommandContext.ExecuteInActiveDocument("打开 V_BBB 设备清单浮窗", (doc, _) =>
        {
            var selectionResult = CaptureSelectionForPanel(doc);

            s_hiddenDevicePanelHost.ShowOrActivate(
                () => new BbbPanelWindow(),
                beforeShow: panel => ApplySelectionResultIfAny(panel, selectionResult));
        });
    }

    [CommandMethod(BbbPluginCommandIds.CommandGroup, BbbPluginCommandIds.TextToAttribute, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void ConvertSelectedTextToAttributeBlocks()
    {
        CadCommandContext.ExecuteInActiveDocument(
            "将文字转换为属性块",
            (doc, _) => BbbTextToAttributeService.Run(doc));
    }

    [CommandMethod(BbbPluginCommandIds.CommandGroup, BbbPluginCommandIds.DeviceBlockCreate, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void CreateDeviceBlock()
    {
        CadCommandContext.ExecuteInActiveDocument(
            "创建设备块",
            (doc, _) => BbbDeviceBlockCreateService.Run(doc));
    }

    [CommandMethod(BbbPluginCommandIds.CommandGroup, BbbPluginCommandIds.BlockAttributeSync, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void SyncBlockAttributes()
    {
        CadCommandContext.ExecuteInActiveDocument(
            "同步块属性",
            (doc, _) => BbbBlockAttributeSyncService.Run(doc));
    }

    internal static void CloseBbbPanelIfAny()
    {
        CloseHost(s_hiddenDevicePanelHost, "关闭 V_BBB 设备清单浮窗失败");
    }

    private static void ApplySelectionResultIfAny(BbbPanelWindow panel, BbbSelectionCaptureResult? impliedSelection)
    {
        if (impliedSelection != null && (impliedSelection.Devices.Count > 0 || impliedSelection.Message.Length > 0))
            panel.ApplySelectionResult(impliedSelection);
    }

    private static BbbSelectionCaptureResult? CaptureSelectionForPanel(Autodesk.AutoCAD.ApplicationServices.Document doc)
    {
        var impliedSelection = TryCaptureImpliedSelection(doc);
        if (!RequiresInteractiveSelection(impliedSelection))
            return impliedSelection;

        CloseHost(s_hiddenDevicePanelHost, "关闭 V_BBB 设备清单浮窗失败");
        return TryCaptureSelectedBlocks(doc);
    }

    private static bool RequiresInteractiveSelection(BbbSelectionCaptureResult? selectionResult)
    {
        return selectionResult == null ||
               (selectionResult.Devices.Count == 0 && selectionResult.Message.Length == 0);
    }

    private static BbbSelectionCaptureResult? TryCaptureImpliedSelection(Autodesk.AutoCAD.ApplicationServices.Document doc)
    {
        try
        {
            return BbbSbjdMappedDeviceService.CaptureImpliedSelectedBlocks(doc);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_BBB 命令读取预选失败", ex);
            return null;
        }
    }

    private static BbbSelectionCaptureResult? TryCaptureSelectedBlocks(Autodesk.AutoCAD.ApplicationServices.Document doc)
    {
        try
        {
            return BbbSbjdMappedDeviceService.CaptureSelectedBlocks(doc);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_BBB 命令读取选块失败", ex);
            return new BbbSelectionCaptureResult
            {
                Message = $"V_BBB：读取选中块失败：{ex.Message}"
            };
        }
    }

    private static void CloseHost(ModelessWindowHost<BbbPanelWindow> host, string errorMessage)
    {
        host.CloseIfAny(
            beforeClose: panel => panel.ClearResultsOnHide(),
            onInvalidOperation: ex => C_toolsDiagnostics.LogNonFatal($"{errorMessage}（无效操作）", ex),
            onError: ex => C_toolsDiagnostics.LogNonFatal(errorMessage, ex));
    }
}
