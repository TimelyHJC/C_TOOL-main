using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static class TextIncrementCopyService
{
    private static readonly Regex IntegerFragmentRegex = new(@"\d+", RegexOptions.Compiled);
    private static readonly Autodesk.AutoCAD.Runtime.RXClass DbTextClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(DBText));
    private static readonly Autodesk.AutoCAD.Runtime.RXClass MTextClass = Autodesk.AutoCAD.Runtime.RXClass.GetClass(typeof(MText));
    private const int DefaultIncrement = 1;
    private const string SettingsKeyword = "S";

    internal static void Run()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        TextCopySeed? seed = null;

        try
        {
            seed = TryPrepareSeed(doc);
            if (seed == null)
                return;

            var fragment = PromptFragmentToIncrement(ed, seed.TextValue);
            if (fragment == null)
                return;

            RunPlacementLoop(doc, seed, fragment);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_AD 执行失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：F_AD 失败：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_AD 执行失败（参数错误）", ex);
            ed.WriteMessage($"\nC_TOOL：F_AD 失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_AD 执行失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：F_AD 失败：{ex.Message}");
        }
        finally
        {
            seed?.Dispose();
        }
    }

    private static TextCopySeed? TryPrepareSeed(Document doc)
    {
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
                if (IsSupportedTextObjectClass(impliedIds[0].ObjectClass))
                    return TryCreateSeed(doc, impliedIds[0]);

                ed.WriteMessage("\nC_TOOL：F_AD 预选对象不是文字，已切换到点选。");
            }
            else if (impliedIds.Length > 1)
            {
                ed.WriteMessage("\nC_TOOL：F_AD 仅支持预选单个文字，已切换到点选。");
            }
        }

        var peo = new PromptEntityOptions("\n选择递增文字")
        {
            AllowNone = true
        };
        peo.SetRejectMessage("\n只能选择 DBText 或 MText。");
        peo.AddAllowedClass(typeof(DBText), true);
        peo.AddAllowedClass(typeof(MText), true);

        var per = ed.GetEntity(peo);
        if (per.Status != PromptStatus.OK)
        {
            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.Command.CancelSelectText}");
            return null;
        }

        return TryCreateSeed(doc, per.ObjectId);
    }

    private static TextCopySeed? TryCreateSeed(Document doc, ObjectId objectId)
    {
        var ed = doc.Editor;
        var seed = CadDatabaseScope.Read(
            doc,
            (_, tr) =>
            {
                var obj = tr.GetObject(objectId, OpenMode.ForRead, false);
                switch (obj)
                {
                    case DBText dbText:
                    {
                        var textValue = dbText.TextString ?? "";
                        if (string.IsNullOrWhiteSpace(textValue))
                        {
                            ed.WriteMessage("\nC_TOOL：所选文字为空，无法递增复制。");
                            return null;
                        }

                        var basePoint = ResolveDbTextBasePoint(dbText);
                        return new TextCopySeed((Entity)dbText.Clone(), textValue, basePoint);
                    }
                    case MText mText:
                    {
                        var textValue = mText.Contents ?? "";
                        if (string.IsNullOrWhiteSpace(textValue))
                        {
                            ed.WriteMessage("\nC_TOOL：所选文字为空，无法递增复制。");
                            return null;
                        }

                        if (HasUnsupportedMTextFormatting(textValue))
                        {
                            ed.WriteMessage("\nC_TOOL：F_AD 暂不支持带格式控制码的 MText，请先转成普通文字后再用。");
                            return null;
                        }

                        return new TextCopySeed((Entity)mText.Clone(), textValue, mText.Location);
                    }
                    default:
                        ed.WriteMessage("\nC_TOOL：所选对象不是受支持的文字类型。");
                        return null;
                }
            });

        return seed;
    }

    private static bool IsSupportedTextObjectClass(Autodesk.AutoCAD.Runtime.RXClass? objectClass)
    {
        return objectClass != null &&
               (objectClass.IsDerivedFrom(DbTextClass) || objectClass.IsDerivedFrom(MTextClass));
    }

    private static NumberFragment? PromptFragmentToIncrement(Editor ed, string text)
    {
        var fragments = FindNumberFragments(text);
        if (fragments.Count == 0)
        {
            ed.WriteMessage("\nC_TOOL：所选文字中未找到可递增的数字。");
            return null;
        }

        if (fragments.Count == 1)
        {
            ed.WriteMessage($"\nC_TOOL：已识别数字 [{fragments[0].RawText}]。");
            return fragments[0];
        }

        ed.WriteMessage("\nC_TOOL：所选文字中找到多个数字：");
        for (var i = 0; i < fragments.Count; i++)
            ed.WriteMessage($"\n  {i + 1}. {BuildFragmentPreview(text, fragments[i])}");

        var defaultIndex = fragments.Count;
        var prompt = new PromptIntegerOptions($"\nC_TOOL：输入要递增的数字序号 <{defaultIndex}>：")
        {
            AllowNone = true,
            AllowZero = false,
            LowerLimit = 1,
            UpperLimit = fragments.Count,
            DefaultValue = defaultIndex,
            UseDefaultValue = true
        };

        var result = ed.GetInteger(prompt);
        if (result.Status != PromptStatus.OK)
        {
            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.Command.CancelSelectNumber}");
            return null;
        }

        return fragments[result.Value - 1];
    }

    private static void RunPlacementLoop(Document doc, TextCopySeed seed, NumberFragment fragment)
    {
        var ed = doc.Editor;
        var increment = DefaultIncrement;
        var placedCount = 0;
        var currentNumericValue = fragment.NumericValue;
        var previewBasePoint = seed.BasePoint;

        while (true)
        {
            var options = new PromptPointOptions(
                $"\n指定复制位置，当前增量 {increment.ToString(CultureInfo.InvariantCulture)}，按 {SettingsKeyword} 修改，空格结束。")
            {
                BasePoint = previewBasePoint,
                UseBasePoint = true,
                AllowNone = true
            };
            options.Keywords.Add(SettingsKeyword);

            var result = ed.GetPoint(options);
            if (result.Status == PromptStatus.Keyword)
            {
                var updated = PromptIncrement(ed, increment);
                if (updated != null)
                    increment = updated.Value;
                continue;
            }

            if (result.Status == PromptStatus.OK)
            {
                currentNumericValue = GetNextNumericValue(currentNumericValue, increment);
                var nextText = ReplaceFragment(seed.TextValue, fragment, currentNumericValue);
                CreateCopyAtPoint(doc, seed, nextText, result.Value);
                previewBasePoint = result.Value;
                placedCount++;
                continue;
            }

            if (result.Status == PromptStatus.None)
            {
                if (placedCount == 0)
                    ed.WriteMessage("\nC_TOOL：F_AD 未创建任何复制文字。");
                else
                    ed.WriteMessage(
                        $"\nC_TOOL：已复制 {placedCount} 个文字，结束时增量为 +{increment.ToString(CultureInfo.InvariantCulture)}。");
                return;
            }

            ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_AD_Cancelled}");
            return;
        }
    }

    private static int? PromptIncrement(Editor ed, int currentIncrement)
    {
        var prompt = new PromptIntegerOptions($"\nC_TOOL：输入新的递增步长 <{currentIncrement}>：")
        {
            AllowNone = true,
            AllowZero = false,
            LowerLimit = 1,
            UpperLimit = int.MaxValue,
            DefaultValue = currentIncrement,
            UseDefaultValue = true
        };

        var result = ed.GetInteger(prompt);
        if (result.Status == PromptStatus.OK)
            return result.Value;
        if (result.Status == PromptStatus.None)
            return currentIncrement;

        ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.Command.CancelChangeIncrement}");
        return null;
    }

    private static long GetNextNumericValue(long currentNumericValue, int increment)
    {
        try
        {
            return checked(currentNumericValue + increment);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException("递增后的数字超出支持范围。", ex);
        }
    }

    private static void CreateCopyAtPoint(Document doc, TextCopySeed seed, string text, Point3d targetPoint)
    {
        CadDatabaseScope.Write(
            doc,
            (db, tr) =>
            {
                var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(db, tr);
                var clone = (Entity)seed.Prototype.Clone();
                var appended = false;
                try
                {
                    SetEntityText(clone, text);
                    clone.TransformBy(Matrix3d.Displacement(targetPoint - seed.BasePoint));
                    currentSpace.AppendEntity(clone);
                    tr.AddNewlyCreatedDBObject(clone, true);
                    if (clone is DBText dbText)
                        dbText.AdjustAlignment(db);

                    appended = true;
                }
                finally
                {
                    if (!appended)
                        clone.Dispose();
                }

                return true;
            },
            requireDocumentLock: true);
    }

    private static List<NumberFragment> FindNumberFragments(string text)
    {
        var fragments = new List<NumberFragment>();
        foreach (Match match in IntegerFragmentRegex.Matches(text))
        {
            if (!match.Success || match.Length <= 0)
                continue;
            if (!long.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var numericValue))
                continue;

            fragments.Add(new NumberFragment(match.Index, match.Length, match.Value, numericValue));
        }

        return fragments;
    }

    private static string BuildFragmentPreview(string text, NumberFragment fragment)
    {
        var start = Math.Max(0, fragment.StartIndex - 10);
        var end = Math.Min(text.Length, fragment.StartIndex + fragment.Length + 10);
        var prefix = text.Substring(start, fragment.StartIndex - start);
        var suffixStart = fragment.StartIndex + fragment.Length;
        var suffix = text.Substring(suffixStart, end - suffixStart);
        if (start > 0)
            prefix = "..." + prefix;
        if (end < text.Length)
            suffix += "...";
        return $"{prefix}[{fragment.RawText}]{suffix}";
    }

    private static string ReplaceFragment(string sourceText, NumberFragment fragment, long nextValue)
    {
        var formatted = FormatNumber(nextValue, fragment.RawText.Length);
        var prefix = sourceText.Substring(0, fragment.StartIndex);
        var suffix = sourceText.Substring(fragment.StartIndex + fragment.Length);
        return prefix + formatted + suffix;
    }

    private static string FormatNumber(long value, int minDigits)
    {
        var negative = value < 0;
        var absText = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        if (absText.Length < minDigits)
            absText = absText.PadLeft(minDigits, '0');
        return negative ? "-" + absText : absText;
    }

    private static void SetEntityText(Entity entity, string text)
    {
        switch (entity)
        {
            case DBText dbText:
                dbText.TextString = text;
                break;
            case MText mText:
                mText.Contents = text;
                break;
            default:
                throw new ArgumentException("F_AD 仅支持 DBText 或 MText。", nameof(entity));
        }
    }

    private static Point3d ResolveDbTextBasePoint(DBText dbText)
    {
        if (dbText.Justify == AttachmentPoint.BaseLeft)
            return dbText.Position;
        if (dbText.AlignmentPoint.IsEqualTo(Point3d.Origin) && !dbText.Position.IsEqualTo(Point3d.Origin))
            return dbText.Position;

        return dbText.AlignmentPoint;
    }

    private static bool HasUnsupportedMTextFormatting(string contents)
    {
        return contents.IndexOf('\\') >= 0 || contents.IndexOf('{') >= 0 || contents.IndexOf('}') >= 0;
    }

    private sealed class TextCopySeed(Entity prototype, string textValue, Point3d basePoint) : IDisposable
    {
        public Entity Prototype { get; } = prototype;

        public string TextValue { get; } = textValue;

        public Point3d BasePoint { get; } = basePoint;

        public void Dispose() => Prototype.Dispose();
    }

    private sealed class NumberFragment(int startIndex, int length, string rawText, long numericValue)
    {
        public int StartIndex { get; } = startIndex;

        public int Length { get; } = length;

        public string RawText { get; } = rawText;

        public long NumericValue { get; } = numericValue;
    }

}
