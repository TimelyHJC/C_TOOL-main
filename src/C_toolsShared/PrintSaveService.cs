using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;

namespace C_toolsShared;

/// <summary>读取/应用当前布局的打印设置，并枚举打印机、图纸、打印样式。</summary>
public static class PrintSaveService
{
    public const string DefaultPrinterName = "DWG To PDF.pc3";
    public const string DefaultStyleSheet = "C_tool.ctb";
    public const string MediaAutoMatchText = "自动匹配";
    public const string ScaleFitText = "布满图纸";
    public const string PlotOrderAddedOrder = "添加顺序";
    public const string PlotOrderLayoutTopToBottomLeftToRight = "布局 / 上到下 / 左到右";
    public const string PlotOrderLayoutLeftToRightTopToBottom = "布局 / 左到右 / 上到下";
    public const string PlotOrderLayerThenName = "图层 / 图框名";
    public const string PlotOrderTypeThenLayoutThenName = "图框类型 / 布局 / 图框名";
    public const string PlotOrderAreaDescending = "面积从大到小";

    public static IReadOnlyList<string> GetPlotOrderRules() =>
        new[]
        {
            PlotOrderAddedOrder,
            PlotOrderLayoutTopToBottomLeftToRight,
            PlotOrderLayoutLeftToRightTopToBottom,
            PlotOrderLayerThenName,
            PlotOrderTypeThenLayoutThenName,
            PlotOrderAreaDescending
        };

    /// <summary>
    /// CAD「无打印机 / None」在设备列表中的本地化名称；此类名称不能作为有效输出设备写入布局。
    /// </summary>
    public static bool IsNoPlotDeviceName(string? name)
    {
        var s = (name ?? "").Trim();
        if (s.Length == 0)
            return true;
        if (string.Equals(s, "无", StringComparison.Ordinal))
            return true;
        if (string.Equals(s, "无打印机", StringComparison.Ordinal))
            return true;
        if (string.Equals(s, "None", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public sealed class Snapshot
    {
        public string LayoutName { get; init; } = "";
        public string PrinterName { get; init; } = "";
        public string MediaName { get; init; } = "";
        public string StyleSheet { get; init; } = "";
    }

    public static bool TryGetCanonicalMediaNamesForCurrentLayout(
        Document doc,
        string printerName,
        out IReadOnlyList<string> mediaNames,
        out string? error)
    {
        mediaNames = Array.Empty<string>();
        error = null;

        try
        {
            mediaNames = CadDatabaseScope.Read(
                doc,
                (_, tr) =>
                {
                    var layoutId = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
                    var layout = CadDatabaseScope.OpenAs<Layout>(tr, layoutId, OpenMode.ForRead);
                    using var plotSettings = new PlotSettings(layout.ModelType);
                    plotSettings.CopyFrom(layout);

                    var validator = PlotSettingsValidator.Current;
                    validator.SetPlotConfigurationName(plotSettings, printerName, null);
                    validator.RefreshLists(plotSettings);

                    return validator
                        .GetCanonicalMediaNameList(plotSettings)
                        .Cast<string>()
                        .Where(static x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                },
                requireDocumentLock: true);

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static Snapshot? TryReadCurrentLayout(Transaction tr, out string? error)
    {
        error = null;
        try
        {
            var lm = LayoutManager.Current;
            var layoutId = lm.GetLayoutId(lm.CurrentLayout);
            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
            var ps = new PlotSettings(layout.ModelType);
            try
            {
                ps.CopyFrom(layout);
                return new Snapshot
                {
                    LayoutName = layout.LayoutName,
                    PrinterName = ps.PlotConfigurationName ?? "",
                    MediaName = ps.CanonicalMediaName ?? "",
                    StyleSheet = ps.CurrentStyleSheet ?? ""
                };
            }
            finally
            {
                ps.Dispose();
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static bool TryApplyToCurrentLayout(Transaction tr, PrintSavePageFileDto dto, out string? error)
    {
        error = null;
        try
        {
            var lm = LayoutManager.Current;
            var layoutId = lm.GetLayoutId(lm.CurrentLayout);
            var layout = CadDatabaseScope.OpenAs<Layout>(tr, layoutId, OpenMode.ForWrite);
            using var ps = new PlotSettings(layout.ModelType);
            ps.CopyFrom(layout);
            var pv = PlotSettingsValidator.Current;
            var printer = (dto.PrinterName ?? "").Trim();
            var media = NormalizeMediaName(dto.CanonicalMediaName);
            if (IsNoPlotDeviceName(printer))
            {
                error = "当前打印机为「无」或未选择有效输出设备，无法写入布局。请在列表中选择 PDF/物理打印机等。";
                return false;
            }

            pv.RefreshLists(ps);
            if (media.Length > 0)
                pv.SetPlotConfigurationName(ps, printer, media);
            else
                pv.SetPlotConfigurationName(ps, printer, null);

            var style = (dto.StyleSheet ?? "").Trim();
            if (style.Length > 0)
            {
                pv.RefreshLists(ps);
                pv.SetCurrentStyleSheet(ps, style);
            }

            ApplyScaleSettings(ps, pv, dto);
            pv.SetPlotCentered(ps, dto.CenterPlot);
            pv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);
            if (!dto.CenterPlot)
                pv.SetPlotOrigin(ps, new Point2d(dto.OffsetX, dto.OffsetY));
            pv.SetPlotRotation(ps, ResolvePlotRotation(dto, ps.CanonicalMediaName));

            ps.PlotPlotStyles = style.Length > 0;
            ps.ShowPlotStyles = false;
            ps.ScaleLineweights = dto.ScaleLineweights;
            ps.PlotTransparency = false;
            ps.PlotHidden = false;
            ps.PlotViewportBorders = false;

            layout.CopyFrom(ps);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryApplyToCurrentLayout(Document doc, PrintSavePageFileDto dto, out string? error)
    {
        error = null;

        try
        {
            string? localError = null;
            var ok = CadDatabaseScope.Write(
                doc,
                (_, tr) => TryApplyToCurrentLayout(tr, dto, out localError),
                requireDocumentLock: true);

            error = localError;
            return ok;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void ApplyScaleSettings(
        PlotSettings settings,
        PlotSettingsValidator validator,
        PrintSavePageFileDto dto)
    {
        var scaleText = (dto.ScaleText ?? "").Trim();
        if (dto.FitToPaper ||
            scaleText.Length == 0 ||
            string.Equals(scaleText, ScaleFitText, StringComparison.OrdinalIgnoreCase))
        {
            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, StdScaleType.ScaleToFit);
            return;
        }

        if (TryResolveStandardScale(scaleText, out var standardScale))
        {
            validator.SetUseStandardScale(settings, true);
            validator.SetStdScaleType(settings, standardScale);
            return;
        }

        if (TryParseScale(scaleText, out var numerator, out var denominator))
        {
            validator.SetUseStandardScale(settings, false);
            validator.SetCustomPrintScale(settings, new CustomScale(numerator, denominator));
            return;
        }

        validator.SetUseStandardScale(settings, true);
        validator.SetStdScaleType(settings, StdScaleType.StdScale1To1);
    }

    private static bool TryResolveStandardScale(string scaleText, out StdScaleType standardScale)
    {
        standardScale = StdScaleType.StdScale1To1;
        switch ((scaleText ?? "").Trim())
        {
            case "1:1":
                standardScale = StdScaleType.StdScale1To1;
                return true;
            case "1:2":
                standardScale = StdScaleType.StdScale1To2;
                return true;
            case "1:50":
                standardScale = StdScaleType.StdScale1To50;
                return true;
            case "1:100":
                standardScale = StdScaleType.StdScale1To100;
                return true;
            default:
                return false;
        }
    }

    private static bool TryParseScale(string scaleText, out double numerator, out double denominator)
    {
        numerator = 1;
        denominator = 1;

        var parts = (scaleText ?? "").Trim().Split(':');
        if (parts.Length != 2)
            return false;

        if (!TryParseDouble(parts[0], out numerator) ||
            !TryParseDouble(parts[1], out denominator))
        {
            return false;
        }

        return numerator > 0 && denominator > 0;
    }

    private static bool TryParseDouble(string? text, out double value)
    {
        var candidate = (text ?? "").Trim();
        if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;

        return double.TryParse(candidate, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
    }

    private static PlotRotation ResolvePlotRotation(PrintSavePageFileDto dto, string? effectiveMediaName)
    {
        if (dto.AutoMatchOrientation && TryParseMediaSize(effectiveMediaName, out var mediaWidth, out var mediaHeight))
            return ResolveRotationFromLandscape(mediaWidth >= mediaHeight, dto.UpsideDown);

        return ResolveRotationFromLandscape(dto.Landscape, dto.UpsideDown);
    }

    private static PlotRotation ResolveRotationFromLandscape(bool landscape, bool upsideDown)
    {
        if (landscape)
            return upsideDown ? PlotRotation.Degrees270 : PlotRotation.Degrees090;

        return upsideDown ? PlotRotation.Degrees180 : PlotRotation.Degrees000;
    }

    public static string NormalizeMediaName(string? mediaName)
    {
        var value = (mediaName ?? "").Trim();
        if (value.Length == 0 ||
            string.Equals(value, MediaAutoMatchText, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return value;
    }

    public static bool TryParseMediaSize(string? mediaName, out double width, out double height)
    {
        width = 0;
        height = 0;
        var value = (mediaName ?? "").Trim();
        if (value.Length == 0)
            return false;

        var matches = Regex.Matches(value, @"\d+(?:\.\d+)?");
        if (matches.Count < 2)
            return false;

        var widthIndex = matches.Count - 2;
        var heightIndex = matches.Count - 1;
        if (!double.TryParse(matches[widthIndex].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out width))
            return false;
        if (!double.TryParse(matches[heightIndex].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out height))
            return false;

        return width > 0 && height > 0;
    }

    public static StringCollection GetPlotDeviceList()
    {
        return PlotSettingsValidator.Current.GetPlotDeviceList();
    }

    public static StringCollection GetCanonicalMediaNameList(PlotSettings ps)
    {
        return PlotSettingsValidator.Current.GetCanonicalMediaNameList(ps);
    }

    public static StringCollection GetPlotStyleSheetList()
    {
        return PlotSettingsValidator.Current.GetPlotStyleSheetList();
    }

    /// <summary>合并用户可编辑目录及插件配置目录下的 <c>*.ctb</c>、<c>*.stb</c> 文件名到列表。</summary>
    public static void MergePlotStyleFileNamesFromUserFolders(IList<string> target)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in target)
        {
            if (!string.IsNullOrEmpty(s))
                seen.Add(s);
        }

        foreach (var dir in new[] { C_toolsPaths.UserEditableFolder, C_toolsPaths.UserConfigFolder })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                continue;
            try
            {
                foreach (var pattern in new[] { "*.ctb", "*.stb" })
                {
                    foreach (var f in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    {
                        var name = Path.GetFileName(f);
                        if (name.Length == 0 || !seen.Add(name))
                            continue;
                        target.Add(name);
                    }
                }
            }
            catch (IOException)
            {
                // 忽略单目录失败
            }
            catch (UnauthorizedAccessException)
            {
                // 忽略
            }
        }

        var sorted = target.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        target.Clear();
        foreach (var x in sorted)
            target.Add(x);
    }
}
