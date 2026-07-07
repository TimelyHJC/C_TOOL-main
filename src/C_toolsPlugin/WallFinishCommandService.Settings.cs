using System.Globalization;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static partial class SettingsManager
    {
        internal static string FormatDistance(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        internal static string GetSelectionPromptMessage() =>
            "\nC_TOOL 选择墙体线 [设置(S)]";

        internal static string NormalizeKeyword(string? keyword) =>
            (keyword ?? "").Trim();

        internal static IReadOnlyList<WallFinishPreviewItem> BuildPreviewItems(
            Document doc,
            SourceCurveSelection selection,
            WallFinishSettingsDto settings)
        {
            var items = new List<WallFinishPreviewItem>(selection.GuideSelections.Count);
            for (var i = 0; i < selection.GuideSelections.Count; i++)
            {
                var guideSelection = selection.GuideSelections[i];
                var resolvedSettings = ResolveSettings(GetEntityLayerName(doc.Database, guideSelection.PropertySourceEntityId), settings);
                items.Add(new WallFinishPreviewItem(
                    guideSelection.GuidePolyline,
                    resolvedSettings.PreviewColorIndex,
                    guideSelection.RecognizedInteriorSideSign,
                    guideSelection.UseForDirectionResolution));
            }

            return items;
        }

        internal static HatchStyleSnapshot ResolveHatchStyle(LayerShortcutEntry? layerEntry)
        {
            var style = HatchStyleSnapshot.TryParseJson(layerEntry?.HatchStyle);
            if (style != null)
                return style;

            return new HatchStyleSnapshot
            {
                PatternName = SolidPatternName,
                Scale = 1.0,
                AngleDegrees = 0.0
            };
        }

        internal static int ResolveSideSign(
            Polyline guidePolyline,
            WallFinishDirectionSelection directionSelection,
            int recognizedInteriorSideSign)
        {
            if (directionSelection.UseRecognizedFigureDirection)
                return directionSelection.UseInteriorSide ? recognizedInteriorSideSign : -recognizedInteriorSideSign;

            return GeometryHelpers.ResolveSideSign(guidePolyline, directionSelection.SamplePoint, 1);
        }

        internal static WallFinishResolvedSettings ResolveSettings(string sourceLayerName, WallFinishSettingsDto settings)
        {
            var layerEntries = LayerShortcutStore.Load();
            var finishHatchEntry = TryFindFinishHatchEntry(layerEntries);
            var configuredTargetLayerName = (settings.TargetLayerName ?? "").Trim();
            if (configuredTargetLayerName.Length > 0)
            {
                var configuredEntry = FindLayerShortcutEntry(layerEntries, configuredTargetLayerName);
                return new WallFinishResolvedSettings(configuredTargetLayerName, configuredEntry, finishHatchEntry, settings.ColorIndex);
            }

            var finishLayerEntry = TryFindFinishLayerEntry(layerEntries);
            if (finishLayerEntry != null)
            {
                return new WallFinishResolvedSettings(
                    finishLayerEntry.LayerName.Trim(),
                    finishLayerEntry,
                    finishHatchEntry,
                    settings.ColorIndex);
            }

            var fallbackLayerName = (sourceLayerName ?? "").Trim();
            return new WallFinishResolvedSettings(
                fallbackLayerName.Length == 0 ? "0" : fallbackLayerName,
                null,
                finishHatchEntry,
                settings.ColorIndex);
        }

        internal static void ShowSettingsDialog(Document doc, ref WallFinishSettingsDto settings)
        {
            var ed = doc.Editor;

            try
            {
                var layerEntries = LayerShortcutStore.Load();
                var currentLayerName = GetCurrentLayerName(doc);
                var targetLayerOptions = LoadTargetLayerOptions(doc.Database, layerEntries, currentLayerName);
                var window = new WallFinishSettingsWindow(settings, targetLayerOptions, AutoLayerDisplayName);
                var accepted = Autodesk.AutoCAD.ApplicationServices.Core.Application.ShowModalWindow(
                    AcAp.MainWindow?.Handle ?? IntPtr.Zero,
                    window,
                    false);

                if (accepted != true || window.SavedSettings == null)
                    return;

                if (!TrySaveSettings(ed, window.SavedSettings))
                    return;

                settings = window.SavedSettings;
                ed.WriteMessage($"\nC_TOOL：完成面设置已更新。{FormatSettingsSummary(settings)}。");
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("打开 F_WCC 完成面设置失败（无效操作）", ex);
                ed.WriteMessage($"\nC_TOOL：打开完成面设置失败：{ex.Message}");
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("打开 F_WCC 完成面设置失败（CAD）", ex);
                ed.WriteMessage($"\nC_TOOL：打开完成面设置失败：{ex.Message}");
            }
        }

        internal static bool TrySaveSettings(Editor ed, WallFinishSettingsDto settings)
        {
            try
            {
                WallFinishSettingsStore.Save(settings);
                return true;
            }
            catch (ArgumentException ex)
            {
                C_toolsDiagnostics.LogNonFatal("保存 F_WCC 完成面设置（路径参数）", ex);
                ed.WriteMessage($"\nC_TOOL：保存完成面设置失败：{ex.Message}");
                return false;
            }
            catch (PathTooLongException ex)
            {
                C_toolsDiagnostics.LogNonFatal("保存 F_WCC 完成面设置（路径过长）", ex);
                ed.WriteMessage($"\nC_TOOL：保存完成面设置失败：{ex.Message}");
                return false;
            }
            catch (NotSupportedException ex)
            {
                C_toolsDiagnostics.LogNonFatal("保存 F_WCC 完成面设置（路径格式）", ex);
                ed.WriteMessage($"\nC_TOOL：保存完成面设置失败：{ex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                C_toolsDiagnostics.LogNonFatal("保存 F_WCC 完成面设置（权限）", ex);
                ed.WriteMessage($"\nC_TOOL：保存完成面设置失败：{ex.Message}");
                return false;
            }
            catch (IOException ex)
            {
                C_toolsDiagnostics.LogNonFatal("保存 F_WCC 完成面设置", ex);
                ed.WriteMessage($"\nC_TOOL：保存完成面设置失败：{ex.Message}");
                return false;
            }
        }

        internal static string FormatSettingsSummary(WallFinishSettingsDto settings)
        {
            var targetLayerName = (settings.TargetLayerName ?? "").Trim();
            var layerSummary = targetLayerName.Length == 0 ? AutoLayerDisplayName : targetLayerName;
            var colorSummary = settings.ColorIndex.HasValue
                ? $"[{settings.ColorIndex.Value}]"
                : "[沿用现有]";
            return $"偏移量 [{FormatDistance(settings.OffsetDistance)}]，图层 [{layerSummary}]，图层颜色 {colorSummary}";
        }

        internal static string FormatLayerSummary(IEnumerable<string> layerNames)
        {
            var list = layerNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count == 0)
                return "[0]";

            if (list.Count == 1)
                return $"[{list[0]}]";

            return $"[{string.Join("]、[", list)}]";
        }
    }
}
