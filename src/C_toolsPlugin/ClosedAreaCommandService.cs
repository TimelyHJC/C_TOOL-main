using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Gi = Autodesk.AutoCAD.GraphicsInterface;

namespace C_toolsPlugin;

/// <summary>
/// F_CAR：选择单个封闭图形，读取面积并按平方米插入文字。
/// </summary>
internal static class ClosedAreaCommandService
{
    private const string CommandName = PluginCommandIds.ClosedAreaReport;
    private const string IncreaseTextKeyword = "D";
    private const string DecreaseTextKeyword = "X";
    private const string IncreaseTextKeywordDisplay = "变大";
    private const string DecreaseTextKeywordDisplay = "变小";
    private const string SquareMeterSuffix = "㎡";
    private const int DefaultInsunits = 4;
    private const double MinAreaTolerance = 1e-9;
    private const double PointTolerance = 1e-6;
    private const double DefaultTextHeight = 2.5;
    private const double MinTextHeight = 0.01;
    private const double MaxTextHeight = 1_000_000.0;
    private const double TextHeightScaleFactor = 1.2;

    internal static void Run()
    {
        CadCommandContext.ExecuteInActiveDocument($"执行 {CommandName}", (doc, ed) =>
        {
            if (!TryResolveTargetEntityId(doc, out var entityId, out var cancelled))
            {
                if (cancelled)
                    ed.WriteMessage($"\nC_TOOL：{CommandName} 已取消。");
                return;
            }

            var failureMessage = "";
            var hasArea = false;
            var rawArea = 0.0;
            var areaSquareMeters = 0.0;
            var conversionLabel = "";

            CadDatabaseScope.Read(doc, (_, tr) =>
            {
                if (!CadDatabaseScope.TryOpenAs<Entity>(tr, entityId, OpenMode.ForRead, out var entity) ||
                    entity == null)
                {
                    failureMessage = $"{CommandName} 仅支持图形。";
                    return;
                }

                if (!TryResolveArea(entity, out rawArea, out failureMessage))
                    return;

                if (!IsUsableArea(rawArea))
                {
                    failureMessage = $"{CommandName} 无有效面积。";
                    return;
                }

                var conversion = ResolveSquareMeterConversion();
                areaSquareMeters = rawArea * conversion.SquareMetersPerSquareDrawingUnit;
                conversionLabel = conversion.Label;
                hasArea = true;
            });

            if (!hasArea)
            {
                ed.WriteMessage($"\nC_TOOL：{failureMessage}");
                return;
            }

            var areaText = FormatAreaLabel(areaSquareMeters);
            var initialTextHeight = ResolveInitialTextHeight();
            if (!PromptAreaTextPlacement(ed, doc.Database, areaText, initialTextHeight, out var location, out var textHeight))
            {
                ed.WriteMessage($"\nC_TOOL：{CommandName} 已取消。");
                return;
            }

            CreateAreaText(doc, areaText, location, textHeight);
            ed.WriteMessage($"\nC_TOOL：已插入面积文字 {areaText}（{conversionLabel}）。");
        });
    }

    private static bool TryResolveTargetEntityId(Document doc, out ObjectId entityId, out bool cancelled)
    {
        entityId = ObjectId.Null;
        cancelled = false;

        var ed = doc.Editor;
        var implied = ed.SelectImplied();
        if (implied.Status == PromptStatus.OK && implied.Value != null)
        {
            var impliedIds = implied.Value.GetObjectIds()
                .Where(id => !id.IsNull)
                .Take(2)
                .ToArray();

            ed.SetImpliedSelection(Array.Empty<ObjectId>());

            if (impliedIds.Length == 1)
            {
                entityId = impliedIds[0];
                return true;
            }

            if (impliedIds.Length > 1)
                ed.WriteMessage($"\nC_TOOL：{CommandName} 仅支持单选封闭图形。");
        }

        var options = new PromptEntityOptions("\nC_TOOL：选封闭图形获取面积：");
        var result = ed.GetEntity(options);
        if (result.Status == PromptStatus.OK)
        {
            entityId = result.ObjectId;
            return true;
        }

        cancelled = result.Status == PromptStatus.Cancel;
        return false;
    }

    private static bool TryResolveArea(Entity entity, out double area, out string failureMessage)
    {
        area = 0.0;
        failureMessage = "";

        if (entity is Curve curve && !IsClosedCurve(curve))
        {
            failureMessage = "所选对象不是封闭图形，请选择闭合多段线、圆、椭圆、闭合样条线、面域或填充。";
            return false;
        }

        if (TryReadAreaProperty(entity, out area))
            return true;

        if (entity is Curve closedCurve && TryCreateRegionArea(closedCurve, out area, out failureMessage))
            return true;

        if (failureMessage.Length == 0)
            failureMessage = "所选对象不支持面积计算，请选择闭合多段线、圆、椭圆、闭合样条线、面域或填充。";

        return false;
    }

    private static bool IsClosedCurve(Curve curve)
    {
        if (curve is Circle)
            return true;

        if (TryReadBooleanProperty(curve, "Closed", out var closed))
            return closed;

        return TryReadBooleanProperty(curve, "IsClosed", out closed) && closed;
    }

    private static bool TryReadBooleanProperty(object target, string propertyName, out bool value)
    {
        value = false;

        var property = target.GetType().GetProperty(propertyName);
        if (property == null || !property.CanRead)
            return false;

        try
        {
            if (property.GetValue(target) is bool boolValue)
            {
                value = boolValue;
                return true;
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"F_CAR 读取 {target.GetType().Name}.{propertyName} 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"F_CAR 读取 {target.GetType().Name}.{propertyName} 失败（CAD）", ex);
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"F_CAR 读取 {target.GetType().Name}.{propertyName} 失败", ex);
        }

        return false;
    }

    private static bool TryReadAreaProperty(object target, out double area)
    {
        area = 0.0;

        var property = target.GetType().GetProperty("Area");
        if (property == null || !property.CanRead)
            return false;

        try
        {
            var value = property.GetValue(target);
            switch (value)
            {
                case double doubleValue when IsUsableArea(doubleValue):
                    area = doubleValue;
                    return true;
                case float floatValue when IsUsableArea(floatValue):
                    area = floatValue;
                    return true;
                case decimal decimalValue when IsUsableArea((double)decimalValue):
                    area = (double)decimalValue;
                    return true;
                default:
                    return false;
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"F_CAR 读取 {target.GetType().Name}.Area 失败（无效操作）", ex);
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"F_CAR 读取 {target.GetType().Name}.Area 失败（CAD）", ex);
            return false;
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"F_CAR 读取 {target.GetType().Name}.Area 失败", ex);
            return false;
        }
    }

    private static bool TryCreateRegionArea(Curve curve, out double area, out string failureMessage)
    {
        area = 0.0;
        failureMessage = "";

        DBObject? curveClone = null;
        DBObjectCollection? regions = null;

        try
        {
            curveClone = curve.Clone() as DBObject;
            if (curveClone == null)
            {
                failureMessage = "所选封闭图形无法复制为临时边界，未能计算面积。";
                return false;
            }

            var curveCollection = new DBObjectCollection();
            curveCollection.Add(curveClone);

            regions = Region.CreateFromCurves(curveCollection);
            if (regions == null || regions.Count == 0)
            {
                failureMessage = "所选封闭图形无法生成面域，请检查是否自交、退化或非共面。";
                return false;
            }

            double totalArea = 0.0;
            foreach (DBObject item in regions)
            {
                if (item is Region region && IsUsableArea(region.Area))
                    totalArea += region.Area;
            }

            if (!IsUsableArea(totalArea))
            {
                failureMessage = "所选封闭图形未生成有效面积，请检查边界是否正确闭合。";
                return false;
            }

            area = totalArea;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_CAR 通过面域计算面积失败（无效操作）", ex);
            failureMessage = "所选封闭图形当前无法计算面积，请稍后重试。";
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_CAR 通过面域计算面积失败（参数错误）", ex);
            failureMessage = "所选封闭图形无法用于面域面积计算。";
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_CAR 通过面域计算面积失败（CAD）", ex);
            failureMessage = "所选封闭图形无法生成面域，请检查是否自交、退化或非共面。";
            return false;
        }
        finally
        {
            if (regions != null)
            {
                foreach (DBObject item in regions)
                    item.Dispose();
            }

            curveClone?.Dispose();
        }
    }

    private static bool IsUsableArea(double area)
    {
        return !double.IsNaN(area) && !double.IsInfinity(area) && area > MinAreaTolerance;
    }

    private static AreaConversionInfo ResolveSquareMeterConversion()
    {
        var insunits = TryGetInsunitsValue();
        return CreateAreaConversion(insunits);
    }

    private static int TryGetInsunitsValue()
    {
        return CadSystemVariableService.TryGetInt32(SystemVariableNames.Insunits, out var insunits)
            ? insunits
            : DefaultInsunits;
    }

    private static AreaConversionInfo CreateAreaConversion(int insunits)
    {
        return insunits switch
        {
            1 => FromLinearUnitMeters(insunits, "INSUNITS=1（英寸）", 0.0254),
            2 => FromLinearUnitMeters(insunits, "INSUNITS=2（英尺）", 0.3048),
            3 => FromLinearUnitMeters(insunits, "INSUNITS=3（英里）", 1609.344),
            4 => FromLinearUnitMeters(insunits, "INSUNITS=4（毫米）", 0.001),
            5 => FromLinearUnitMeters(insunits, "INSUNITS=5（厘米）", 0.01),
            6 => FromLinearUnitMeters(insunits, "INSUNITS=6（米）", 1.0),
            7 => FromLinearUnitMeters(insunits, "INSUNITS=7（千米）", 1000.0),
            8 => FromLinearUnitMeters(insunits, "INSUNITS=8（微英寸）", 2.54e-8),
            9 => FromLinearUnitMeters(insunits, "INSUNITS=9（密耳）", 2.54e-5),
            10 => FromLinearUnitMeters(insunits, "INSUNITS=10（码）", 0.9144),
            11 => FromLinearUnitMeters(insunits, "INSUNITS=11（埃）", 1e-10),
            12 => FromLinearUnitMeters(insunits, "INSUNITS=12（纳米）", 1e-9),
            13 => FromLinearUnitMeters(insunits, "INSUNITS=13（微米）", 1e-6),
            14 => FromLinearUnitMeters(insunits, "INSUNITS=14（分米）", 0.1),
            15 => FromLinearUnitMeters(insunits, "INSUNITS=15（十米）", 10.0),
            16 => FromLinearUnitMeters(insunits, "INSUNITS=16（百米）", 100.0),
            17 => FromLinearUnitMeters(insunits, "INSUNITS=17（吉米）", 1e9),
            18 => FromLinearUnitMeters(insunits, "INSUNITS=18（天文单位）", 149597870700.0),
            19 => FromLinearUnitMeters(insunits, "INSUNITS=19（光年）", 9.4607304725808e15),
            20 => FromLinearUnitMeters(insunits, "INSUNITS=20（秒差距）", 3.08567758149137e16),
            21 => FromLinearUnitMeters(insunits, "INSUNITS=21（美制测量英尺）", 1200.0 / 3937.0),
            22 => FromLinearUnitMeters(insunits, "INSUNITS=22（美制测量英寸）", 100.0 / 3937.0),
            23 => FromLinearUnitMeters(insunits, "INSUNITS=23（美制测量码）", 3600.0 / 3937.0),
            24 => FromLinearUnitMeters(insunits, "INSUNITS=24（美制测量英里）", 6336000.0 / 3937.0),
            0 => FromLinearUnitMeters(insunits, "INSUNITS=0（未设置，按毫米默认值）", 0.001),
            _ => FromLinearUnitMeters(insunits, $"INSUNITS={insunits}（未识别，按毫米默认值）", 0.001)
        };
    }

    private static AreaConversionInfo FromLinearUnitMeters(int insunits, string label, double metersPerDrawingUnit)
    {
        return new AreaConversionInfo(insunits, label, metersPerDrawingUnit * metersPerDrawingUnit);
    }

    private static bool PromptAreaTextPlacement(
        Editor ed,
        Database db,
        string areaText,
        double initialTextHeight,
        out Point3d location,
        out double textHeight)
    {
        location = Point3d.Origin;
        textHeight = ClampTextHeight(initialTextHeight);

        using var jig = new AreaTextPlacementJig(db, areaText, textHeight);
        while (true)
        {
            var dragResult = ed.Drag(jig);
            if (jig.ConsumeTextHeightChangeRequest())
                continue;

            if (dragResult.Status != PromptStatus.OK || !jig.HasLocation)
                return false;

            location = jig.Location;
            textHeight = jig.TextHeight;
            return true;
        }
    }

    private static void CreateAreaText(Document doc, string areaText, Point3d location, double textHeight)
    {
        CadDatabaseScope.Write(
            doc,
            (db, tr) =>
            {
                var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(db, tr);
                var text = CreateAreaTextEntity(db, areaText, location, textHeight);
                currentSpace.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
            },
            requireDocumentLock: true);
    }

    private static DBText CreateAreaTextEntity(Database db, string areaText, Point3d location, double textHeight)
    {
        var text = new DBText();
        text.SetDatabaseDefaults(db);
        text.TextString = areaText;
        text.Height = ClampTextHeight(textHeight);
        text.Position = location;
        if (!db.Textstyle.IsNull)
            text.TextStyleId = db.Textstyle;

        return text;
    }

    private static double ResolveInitialTextHeight()
    {
        return CadSystemVariableService.TryGetPositiveDouble(SystemVariableNames.TextSize, out var textSize)
            ? ClampTextHeight(textSize)
            : DefaultTextHeight;
    }

    private static double ScaleTextHeight(double textHeight, double scale)
    {
        if (double.IsNaN(textHeight) || double.IsInfinity(textHeight) || textHeight <= 0.0)
            return DefaultTextHeight;

        return ClampTextHeight(textHeight * scale);
    }

    private static double ClampTextHeight(double textHeight)
    {
        if (double.IsNaN(textHeight) || double.IsInfinity(textHeight) || textHeight <= 0.0)
            return DefaultTextHeight;

        if (textHeight < MinTextHeight)
            return MinTextHeight;

        return textHeight > MaxTextHeight ? MaxTextHeight : textHeight;
    }

    private static string FormatAreaLabel(double area)
    {
        return $"{FormatArea(area)}{SquareMeterSuffix}";
    }

    private static string FormatArea(double area)
    {
        return area.ToString("#,0.0", CultureInfo.CurrentCulture);
    }

    private readonly struct AreaConversionInfo
    {
        internal AreaConversionInfo(int insunits, string label, double squareMetersPerSquareDrawingUnit)
        {
            Insunits = insunits;
            Label = label;
            SquareMetersPerSquareDrawingUnit = squareMetersPerSquareDrawingUnit;
        }

        internal int Insunits { get; }

        internal string Label { get; }

        internal double SquareMetersPerSquareDrawingUnit { get; }
    }

    private sealed class AreaTextPlacementJig : DrawJig, IDisposable
    {
        private readonly Database _db;
        private readonly string _areaText;
        private Point3d _location;
        private double _textHeight;

        internal AreaTextPlacementJig(Database db, string areaText, double textHeight)
        {
            _db = db;
            _areaText = areaText;
            _textHeight = ClampTextHeight(textHeight);
        }

        internal bool HasLocation { get; private set; }

        internal Point3d Location => _location;

        internal double TextHeight => _textHeight;

        private bool TextHeightChangeRequested { get; set; }

        internal bool ConsumeTextHeightChangeRequest()
        {
            if (!TextHeightChangeRequested)
                return false;

            TextHeightChangeRequested = false;
            return true;
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nC_TOOL：指定面积文字位置")
            {
                Cursor = CursorType.Crosshair,
                AppendKeywordsToMessage = true,
                UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.GovernedByOrthoMode
            };
            AddTextHeightKeywords(options.Keywords);

            var result = prompts.AcquirePoint(options);
            if (result.Status == PromptStatus.Keyword)
            {
                ApplyTextHeightKeyword(result.StringResult);
                return SamplerStatus.NoChange;
            }

            if (result.Status != PromptStatus.OK)
                return SamplerStatus.Cancel;

            if (HasLocation && result.Value.DistanceTo(_location) <= PointTolerance)
                return SamplerStatus.NoChange;

            _location = result.Value;
            HasLocation = true;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(Gi.WorldDraw draw)
        {
            if (!HasLocation)
                return true;

            try
            {
                using var previewText = CreateAreaTextEntity(_db, _areaText, _location, _textHeight);
                draw.Geometry.Draw(previewText);
                return true;
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_CAR 面积文字预览失败（无效操作）", ex);
                return false;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("F_CAR 面积文字预览失败（CAD）", ex);
                return false;
            }
        }

        public void Dispose()
        {
        }

        private void ApplyTextHeightKeyword(string? keyword)
        {
            var normalized = NormalizeKeyword(keyword);
            if (IsIncreaseTextKeyword(normalized))
            {
                _textHeight = ScaleTextHeight(_textHeight, TextHeightScaleFactor);
                TextHeightChangeRequested = true;
                return;
            }

            if (IsDecreaseTextKeyword(normalized))
            {
                _textHeight = ScaleTextHeight(_textHeight, 1.0 / TextHeightScaleFactor);
                TextHeightChangeRequested = true;
            }
        }
    }

    private static void AddTextHeightKeywords(KeywordCollection keywords)
    {
        keywords.Add(
            IncreaseTextKeyword,
            IncreaseTextKeyword,
            FormatKeywordDisplay(IncreaseTextKeywordDisplay, IncreaseTextKeyword));
        keywords.Add(
            DecreaseTextKeyword,
            DecreaseTextKeyword,
            FormatKeywordDisplay(DecreaseTextKeywordDisplay, DecreaseTextKeyword));
    }

    private static string FormatKeywordDisplay(string label, string shortcut)
    {
        return $"{label}({shortcut})";
    }

    private static string NormalizeKeyword(string? keyword)
    {
        return (keyword ?? "").Trim();
    }

    private static bool IsIncreaseTextKeyword(string keyword)
    {
        return string.Equals(keyword, IncreaseTextKeyword, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(keyword, IncreaseTextKeywordDisplay, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   keyword,
                   FormatKeywordDisplay(IncreaseTextKeywordDisplay, IncreaseTextKeyword),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDecreaseTextKeyword(string keyword)
    {
        return string.Equals(keyword, DecreaseTextKeyword, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(keyword, DecreaseTextKeywordDisplay, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   keyword,
                   FormatKeywordDisplay(DecreaseTextKeywordDisplay, DecreaseTextKeyword),
                   StringComparison.OrdinalIgnoreCase);
    }
}
