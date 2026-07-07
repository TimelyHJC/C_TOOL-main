using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsPlugin;

internal static partial class WallFinishCommandService
{
    internal static partial class SettingsManager
    {
        internal static Color? ResolveHatchEntityColor(
            Transaction tr,
            Database db,
            WallFinishResolvedSettings settings)
        {
            if (!string.Equals(settings.HatchLayerName, settings.TargetLayerName, StringComparison.OrdinalIgnoreCase))
                return null;

            if (TryGetExistingLayerColor(tr, db, settings.HatchLayerName, out var layerColor))
                return layerColor;

            if (settings.HatchLayerEntry?.ColorIndex is >= 1 and <= 255)
                return Color.FromColorIndex(ColorMethod.ByAci, (short)settings.HatchLayerEntry.ColorIndex.Value);

            if (settings.TargetLayerEntry?.ColorIndex is >= 1 and <= 255)
                return Color.FromColorIndex(ColorMethod.ByAci, (short)settings.TargetLayerEntry.ColorIndex.Value);

            return Color.FromColorIndex(ColorMethod.ByAci, DefaultLayerColorIndex);
        }

        internal static bool TryGetExistingLayerColor(
            Transaction tr,
            Database db,
            string layerName,
            out Color color)
        {
            color = Color.FromColorIndex(ColorMethod.ByAci, DefaultLayerColorIndex);
            var trimmedLayerName = (layerName ?? "").Trim();
            if (trimmedLayerName.Length == 0)
                return false;

            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!layerTable.Has(trimmedLayerName))
                return false;

            if (tr.GetObject(layerTable[trimmedLayerName], OpenMode.ForRead) is not LayerTableRecord layerRecord)
                return false;

            color = CloneExplicitColor(layerRecord.Color);
            return true;
        }

        internal static Color CloneExplicitColor(Color source)
        {
            try
            {
                if (source.ColorMethod == ColorMethod.ByLayer)
                    return Color.FromColorIndex(ColorMethod.ByLayer, 256);

                if (source.ColorMethod == ColorMethod.ByBlock)
                    return Color.FromColorIndex(ColorMethod.ByBlock, 0);

                if (source.ColorMethod == ColorMethod.ByAci)
                    return Color.FromColorIndex(ColorMethod.ByAci, source.ColorIndex);

                return Color.FromRgb(source.Red, source.Green, source.Blue);
            }
            catch
            {
                return Color.FromColorIndex(ColorMethod.ByAci, DefaultLayerColorIndex);
            }
        }

        internal static void ApplyEntityColor(Entity entity, Color? color)
        {
            if (color != null)
            {
                entity.Color = color;
                return;
            }

            SetEntityColorByLayer(entity);
        }

        internal static void SetEntityColorByLayer(Entity entity) =>
            entity.Color = Color.FromColorIndex(ColorMethod.ByLayer, 256);

        internal static void ApplyHatchStyle(Hatch hatch, HatchStyleSnapshot hatchStyle)
        {
            var patternName = (hatchStyle.PatternName ?? "").Trim();
            if (patternName.Length == 0)
                patternName = SolidPatternName;

            Autodesk.AutoCAD.Runtime.Exception? lastCadException = null;
            foreach (var patternType in new[] { HatchPatternType.PreDefined, HatchPatternType.CustomDefined })
            {
                try
                {
                    hatch.SetHatchPattern(patternType, patternName);
                    var scale = hatchStyle.Scale;
                    if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
                        scale = 1.0;

                    var angleDegrees = hatchStyle.AngleDegrees;
                    if (double.IsNaN(angleDegrees) || double.IsInfinity(angleDegrees))
                        angleDegrees = 0.0;

                    hatch.PatternScale = scale;
                    hatch.PatternAngle = angleDegrees * (Math.PI / 180.0);
                    return;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    lastCadException = ex;
                }
            }

            if (lastCadException != null)
                C_toolsDiagnostics.LogNonFatal("F_WCC 应用填充样式失败，回退为 SOLID", lastCadException);

            hatch.SetHatchPattern(HatchPatternType.PreDefined, SolidPatternName);
            hatch.PatternScale = 1.0;
            hatch.PatternAngle = 0.0;
        }

        internal static void MoveEntityToBottom(Transaction tr, BlockTableRecord currentSpace, ObjectId entityId)
        {
            try
            {
                var drawOrderTable = (DrawOrderTable)tr.GetObject(currentSpace.DrawOrderTableId, OpenMode.ForWrite);
                var entityIds = new ObjectIdCollection();
                entityIds.Add(entityId);
                drawOrderTable.MoveToBottom(entityIds);
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_WCC 调整填充绘制顺序失败（无效操作）", ex);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_WCC 调整填充绘制顺序失败（CAD）", ex);
            }
        }
    }
}
