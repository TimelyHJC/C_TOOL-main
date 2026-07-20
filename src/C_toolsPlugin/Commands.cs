using System.Globalization;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

namespace C_toolsPlugin;

internal sealed class LayerAliasBridgeRequest
{
    internal string Alias { get; init; } = "";
}

internal sealed class HatchBridgeRequest
{
    internal string LayerName { get; init; } = "";
    internal string PatternName { get; init; } = C_toolsConstants.DefaultHatchPattern;
    internal double Scale { get; init; } = C_toolsConstants.DefaultHatchScale;
    internal double AngleDegrees { get; init; }
}

internal sealed class HatchLayerBridgeRequest
{
    internal string LayerName { get; init; } = "";
    internal string PatternName { get; init; } = "ANSI31";
    internal double Scale { get; init; } = 1.0;
    internal double AngleDegrees { get; init; }
}

internal sealed class HatchLaunchRequest
{
    internal string LayerName { get; init; } = "";
    internal string PatternName { get; init; } = C_toolsConstants.DefaultHatchPattern;
    internal double Scale { get; init; } = C_toolsConstants.DefaultHatchScale;
    internal double AngleDegrees { get; init; }
}

internal static class HatchLaunchService
{
    internal static void Start(Document doc, HatchLaunchRequest request)
    {
        HatchLayerCompat.RememberPendingLayer(doc, request.LayerName);

        var snap = new HatchStyleSnapshot
        {
            PatternName = request.PatternName,
            Scale = request.Scale,
            AngleDegrees = request.AngleDegrees
        };

        HatchCommandHelper.StartHatchWithLayer(doc, request.LayerName, snap);
    }
}

internal static class HatchBridgeRequestMapper
{
    private static HatchLaunchRequest CreateLaunchRequest(
        string layerName,
        string patternName,
        double scale,
        double angleDegrees)
    {
        return new HatchLaunchRequest
        {
            LayerName = layerName,
            PatternName = patternName,
            Scale = scale,
            AngleDegrees = angleDegrees
        };
    }

    internal static HatchLaunchRequest ToLaunchRequest(HatchBridgeRequest request)
    {
        return CreateLaunchRequest(
            request.LayerName,
            request.PatternName,
            request.Scale,
            request.AngleDegrees);
    }

    internal static HatchLaunchRequest ToLaunchRequest(HatchLayerBridgeRequest request)
    {
        return CreateLaunchRequest(
            request.LayerName,
            request.PatternName,
            request.Scale,
            request.AngleDegrees);
    }
}

internal static class HatchLayerBridgeProtocol
{
    private static readonly string[] s_requestVariableNames = { "USERS1", "USERS2", "USERS3", "USERS4" };

    internal static HatchLayerBridgeRequest? TryReadRequest()
    {
        var layerName = (CadSystemVariableService.TryGetString("USERS1") ?? "").Trim();
        if (layerName.Length == 0)
            return null;

        var pattern = (CadSystemVariableService.TryGetString("USERS2") ?? "ANSI31").Trim();
        if (pattern.Length == 0)
            pattern = "ANSI31";

        var scale = TryParseInvariantOrDefault(CadSystemVariableService.TryGetString("USERS3"), 1.0);
        var angleDegrees = TryParseInvariantOrDefault(CadSystemVariableService.TryGetString("USERS4"), 0.0);

        return new HatchLayerBridgeRequest
        {
            LayerName = layerName,
            PatternName = pattern,
            Scale = scale,
            AngleDegrees = angleDegrees
        };
    }

    internal static void ClearRequest()
    {
        CadSystemVariableService.ClearStrings(s_requestVariableNames);
    }

    private static double TryParseInvariantOrDefault(string? value, double fallback)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return fallback;

        return double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }
}

internal static class CommandsBridgeProtocol
{
    private static readonly string[] s_hatchRequestVariableNames =
    {
        SystemVariableNames.Users2,
        SystemVariableNames.Users3,
        SystemVariableNames.Users4,
        SystemVariableNames.Users5
    };

    internal static LayerAliasBridgeRequest? TryReadLayerAliasRequest()
    {
        var alias = (CadSystemVariableService.TryGetString(SystemVariableNames.Users1) ?? "").Trim();
        if (alias.Length == 0)
            return null;

        return new LayerAliasBridgeRequest
        {
            Alias = alias
        };
    }

    internal static HatchBridgeRequest? TryReadHatchRequest()
    {
        var layer = (CadSystemVariableService.TryGetString(SystemVariableNames.Users2) ?? "").Trim();
        if (layer.Length == 0)
            return null;

        var patternRaw = CadSystemVariableService.TryGetString(SystemVariableNames.Users3) ?? "";
        var scaleRaw = CadSystemVariableService.TryGetString(SystemVariableNames.Users4) ?? "";
        var angleRaw = CadSystemVariableService.TryGetString(SystemVariableNames.Users5) ?? "";

        var pattern = string.IsNullOrWhiteSpace(patternRaw)
            ? C_toolsConstants.DefaultHatchPattern
            : patternRaw.Trim();

        if (!TryParseDoubleLoose(scaleRaw, C_toolsConstants.DefaultHatchScale, out var scale) ||
            scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            scale = C_toolsConstants.DefaultHatchScale;
        }

        if (!TryParseDoubleLoose(angleRaw, 0.0, out var angleRad) ||
            double.IsNaN(angleRad) || double.IsInfinity(angleRad))
        {
            angleRad = 0.0;
        }

        return new HatchBridgeRequest
        {
            LayerName = layer,
            PatternName = pattern,
            Scale = scale,
            AngleDegrees = angleRad * (180.0 / Math.PI)
        };
    }

    internal static void ClearLayerAliasRequest()
    {
        CadSystemVariableService.ClearStrings(SystemVariableNames.Users1);
    }

    internal static void ClearHatchRequest()
    {
        CadSystemVariableService.ClearStrings(s_hatchRequestVariableNames);
    }

    private static bool TryParseDoubleLoose(string? s, double fallback, out double value)
    {
        var t = (s ?? "").Trim();
        if (t.Length == 0)
        {
            value = fallback;
            return true;
        }

        if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            return true;
        return double.TryParse(t, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }
}

public class Commands
{
    private static readonly ModelessWindowHost<FloatingPanelWindow> s_panelHost = new();

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.Layer, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void LayerFromLispBridge()
    {
        if (!CadCommandContext.TryGetActiveDocument("执行 F_Layer", out var doc, out var editor) ||
            doc == null ||
            editor == null)
        {
            return;
        }

        var request = CommandsBridgeProtocol.TryReadLayerAliasRequest();
        if (request == null)
        {
            editor.WriteMessage(
                "\nC_TOOL：F_Layer 由图层别名单独命令（如 A2）调用。请直接输入别名单独命令，或在配置面板保存后使用生成的别名。");
            return;
        }

        try
        {
            LayerShortcutExecutor.RunByAlias(request.Alias);
        }
        finally
        {
            CommandsBridgeProtocol.ClearLayerAliasRequest();
        }
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.Hatch, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void C_toolsHatchFromUsersBridge()
    {
        CadCommandContext.ExecuteInActiveDocument("执行 F_Hatch", (doc, _) => StartHatchWithStyle(doc));
    }

    internal static void StartHatchWithStyle(Document doc)
    {
        var request = CommandsBridgeProtocol.TryReadHatchRequest();

        try
        {
            if (request == null)
            {
                doc.Editor.WriteMessage("\nC_TOOL：F_Hatch 需要 USERS2（图层名）。");
                return;
            }

            HatchLaunchService.Start(doc, HatchBridgeRequestMapper.ToLaunchRequest(request));
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_Hatch 执行失败", ex);
            CadCommandContext.TryWriteMessage(doc, $"\nC_TOOL：F_Hatch 失败：{ex.Message}", "写入 F_Hatch 错误消息失败");
        }
        finally
        {
            CommandsBridgeProtocol.ClearHatchRequest();
        }
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.PickHatchStyle, CommandFlags.Modal)]
    public void PickHatchStyleForLayerShortcut()
    {
        HatchStylePickCommand.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.ViewportLock, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void LockLayoutViewport()
    {
        ViewportCommandService.LockViewports();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.ViewportUnlock, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void UnlockLayoutViewport()
    {
        ViewportCommandService.UnlockViewports();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.ViewportScaleReport, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void ReportCurrentViewportScale()
    {
        ViewportCommandService.ReportCurrentViewportScale();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.ViewportWindow, CommandFlags.Modal)]
    public void CreateViewportWindow()
    {
        ViewportCommandService.CreateViewportWindowFromModelSelection();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.LayerDisplayToggle, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void ToggleLayerDisplay()
    {
        LayerVisibilityToggleService.Toggle();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, "LAYFRZ", CommandFlags.Modal | CommandFlags.UsePickSet)]
    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.LayerFreezeFallback, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void FreezeLayerBySelectedObject()
    {
        LayfrzFallbackService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.DashedLine, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void SwitchSelectedLineStyle()
    {
        DashedLineCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.DimScalePick, CommandFlags.Modal)]
    public void PickAnnotationScale()
    {
        ViewportCommandService.PickAnnotationScale();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.ClosedAreaReport, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void ReportClosedArea()
    {
        ClosedAreaCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.TextIncrementCopy, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void CopyTextWithIncrement()
    {
        TextIncrementCopyService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.RectangleCenterAlign, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void CenterSelectionToRectangleFrame()
    {
        RectangleCenterAlignCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.FoldBreakSymbol, CommandFlags.Modal)]
    public void CreateFoldBreakSymbol()
    {
        FoldBreakCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.SwitchToPaperSpace, CommandFlags.Modal)]
    public void SwitchToPaperSpace()
    {
        ViewportCommandService.SwitchToViewportOutside();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.ReflectFlip, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void StartReflectFlip()
    {
        ReflectFlipCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.Rotate180, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void RotateSelection180()
    {
        Rotate180CommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.XlineHorizontal, CommandFlags.Modal)]
    public void StartHorizontalXline()
    {
        StartQuickXline(PluginCommandIds.XlineHorizontal, CommandNames.XlineHorizontalOption);
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.XlineVertical, CommandFlags.Modal)]
    public void StartVerticalXline()
    {
        StartQuickXline(PluginCommandIds.XlineVertical, CommandNames.XlineVerticalOption);
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.DrawOrderBack, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void SendSelectionDrawOrderToBack()
    {
        DrawOrderCommandService.SendToBack();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.DrawOrderFront, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void BringSelectionDrawOrderToFront()
    {
        DrawOrderCommandService.BringToFront(PluginCommandIds.DrawOrderFront);
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.HatchBoundary, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void GenerateHatchBoundary()
    {
        HatchBoundaryCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.QuickArrow, CommandFlags.Modal)]
    public void StartQuickArrow()
    {
        QuickArrowCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.QuickWall, CommandFlags.Modal)]
    public void StartQuickWall()
    {
        QuickWallCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.WallFinish, CommandFlags.Modal | CommandFlags.UsePickSet)]
    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.WallFinishLegacyAlias, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public void StartWallFinish()
    {
        WallFinishCommandService.Run();
    }

    [CommandMethod(PluginCommandIds.CommandGroup, PluginCommandIds.Launcher, CommandFlags.Modal)]
    public void ToggleLauncherPanel()
    {
        ToggleFloatingPanel();
    }

    private static void ToggleFloatingPanel()
    {
        CadCommandContext.ExecuteInActiveDocument(
            "打开浮动面板",
            (_, _) => s_panelHost.Toggle(() => new FloatingPanelWindow()));
    }

    private static void StartQuickXline(string commandId, string optionKeyword)
    {
        if (!CadCommandContext.TryGetActiveDocument(commandId, out var doc, out _) || doc == null)
        {
            return;
        }

        try
        {
            doc.SendStringToExecute($"{CommandNames.Xline}\n{optionKeyword}\n", true, false, false);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandId} 启动 XL 失败（无效操作）", ex);
            CadCommandContext.TryWriteMessage(doc, $"\nC_TOOL：{commandId} 失败：{ex.Message}", "写入 XL 启动错误消息失败");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandId} 启动 XL 失败（CAD）", ex);
            CadCommandContext.TryWriteMessage(doc, $"\nC_TOOL：{commandId} 失败：{ex.Message}", "写入 XL 启动错误消息失败");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"{commandId} 启动 XL 失败", ex);
            CadCommandContext.TryWriteMessage(doc, $"\nC_TOOL：{commandId} 失败：{ex.Message}", "写入 XL 启动错误消息失败");
        }
    }

    internal static void CloseFloatingPanelIfAny()
    {
        s_panelHost.CloseIfAny(
            onInvalidOperation: ex => C_toolsDiagnostics.LogNonFatal("关闭浮动面板失败（无效操作）", ex),
            onError: ex => C_toolsDiagnostics.LogNonFatal("关闭浮动面板失败", ex));
    }
}
