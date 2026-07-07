using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsPlugin;

internal static class QuickArrowCommandService
{
    private const string SettingsKeyword = "S";
    private const string ClearTextToken = "-";
    private const double MinArrowLength = 1e-6;
    private const string ForcedMLeaderStyleName = "Standard";
    private const double FixedArrowSize = 120.0;
    private const double FixedTextHeight = 80.0;
    private static string s_lastTailText = "";

    internal static void Run()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        var ed = doc.Editor;
        var currentText = s_lastTailText;

        try
        {
            while (true)
            {
                var tailPrompt = PromptTailPoint(ed, currentText);
                if (tailPrompt.Action == PointPromptAction.Cancel)
                {
                    ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_JT_Cancelled}");
                    return;
                }

                if (tailPrompt.Action == PointPromptAction.Settings)
                {
                    currentText = PromptTailText(ed, currentText);
                    continue;
                }

                var tailPoint = tailPrompt.Point;
                if (!TryPromptLeaderPath(ed, tailPoint, ref currentText, out var pathPointsFromTail))
                {
                    ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.DimensionCommand.F_JT_Cancelled}");
                    return;
                }

                CreateQuickArrow(doc, tailPoint, pathPointsFromTail, currentText);
                s_lastTailText = currentText;
                if (string.IsNullOrWhiteSpace(currentText))
                    ed.WriteMessage("\nC_TOOL：已插入快速箭头。");
                else
                    ed.WriteMessage($"\nC_TOOL：已插入快速箭头，箭尾文字：{DescribeCurrentText(currentText)}。");

                return;
            }
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 执行失败（无效操作）", ex);
            ed.WriteMessage($"\nC_TOOL：F_JT 失败：{ex.Message}");
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 执行失败（参数错误）", ex);
            ed.WriteMessage($"\nC_TOOL：F_JT 失败：{ex.Message}");
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 执行失败（CAD）", ex);
            ed.WriteMessage($"\nC_TOOL：F_JT 失败：{ex.Message}");
        }
        catch (System.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 执行失败", ex);
            ed.WriteMessage($"\nC_TOOL：F_JT 失败：{ex.Message}");
        }
    }

    private static PointPromptResult PromptTailPoint(Editor ed, string currentText) =>
        PromptPoint(ed, $"\nC_TOOL：第一点指定箭尾，按 {SettingsKeyword} 设置箭尾文字，当前文字：{DescribeCurrentText(currentText)}", null);

    private static PointPromptResult PromptLeaderPathPoint(
        Editor ed,
        Point3d basePoint,
        string currentText,
        int pointCountFromTail)
    {
        var message = pointCountFromTail == 0
            ? $"\nC_TOOL：第二点指定箭头或拐点，继续点可添加更多拐点，按 {SettingsKeyword} 设置箭尾文字，当前文字：{DescribeCurrentText(currentText)}"
            : $"\nC_TOOL：继续指定拐点或箭头，按空格确认生成，按 {SettingsKeyword} 设置箭尾文字，当前文字：{DescribeCurrentText(currentText)}";
        return PromptPoint(
            ed,
            message,
            basePoint,
            allowNone: pointCountFromTail > 0);
    }

    private static PointPromptResult PromptPoint(Editor ed, string message, Point3d? basePoint, bool allowNone = false)
    {
        var options = new PromptPointOptions(message)
        {
            AppendKeywordsToMessage = true,
            AllowNone = allowNone
        };
        options.Keywords.Add(SettingsKeyword);

        if (basePoint.HasValue)
        {
            options.BasePoint = basePoint.Value;
            options.UseBasePoint = true;
        }

        var result = ed.GetPoint(options);
        return result.Status switch
        {
            PromptStatus.OK => PointPromptResult.FromPoint(result.Value),
            PromptStatus.Keyword => PointPromptResult.ForSettings(),
            PromptStatus.None when allowNone => PointPromptResult.ForFinish(),
            _ => PointPromptResult.ForCancel()
        };
    }

    private static bool TryPromptLeaderPath(
        Editor ed,
        Point3d tailPoint,
        ref string currentText,
        out List<Point3d> pathPointsFromTail)
    {
        pathPointsFromTail = new List<Point3d>();

        while (true)
        {
            var basePoint = pathPointsFromTail.Count == 0
                ? tailPoint
                : pathPointsFromTail[pathPointsFromTail.Count - 1];
            var pointPrompt = PromptLeaderPathPoint(ed, basePoint, currentText, pathPointsFromTail.Count);

            if (pointPrompt.Action == PointPromptAction.Cancel)
                return false;

            if (pointPrompt.Action == PointPromptAction.Settings)
            {
                currentText = PromptTailText(ed, currentText);
                continue;
            }

            if (pointPrompt.Action == PointPromptAction.Finish)
            {
                if (pathPointsFromTail.Count == 0)
                {
                    ed.WriteMessage("\nC_TOOL：请至少指定一个箭头点后，再按空格确认。");
                    continue;
                }

                return true;
            }

            if (basePoint.DistanceTo(pointPrompt.Point) <= MinArrowLength)
            {
                ed.WriteMessage(pathPointsFromTail.Count == 0
                    ? "\nC_TOOL：箭尾点与箭头点不能重合，请重新指定。"
                    : "\nC_TOOL：相邻拐点不能重合，请重新指定。");
                continue;
            }

            pathPointsFromTail.Add(pointPrompt.Point);
        }
    }

    private static string PromptTailText(Editor ed, string currentText)
    {
        var prompt = new PromptStringOptions(
            $"\nC_TOOL：输入箭尾文字，直接回车沿用当前值，输入 {ClearTextToken} 清空，当前文字：{DescribeCurrentText(currentText)}：")
        {
            AllowSpaces = true
        };

        var result = ed.GetString(prompt);
        if (result.Status == PromptStatus.OK)
        {
            var nextText = (result.StringResult ?? "").Trim();
            if (string.Equals(nextText, ClearTextToken, StringComparison.Ordinal))
            {
                ed.WriteMessage("\nC_TOOL：已清空箭尾文字。");
                return "";
            }

            if (nextText.Length == 0)
                return currentText;

            ed.WriteMessage($"\nC_TOOL：箭尾文字已设为 {DescribeCurrentText(nextText)}。");
            return nextText;
        }

        if (result.Status == PromptStatus.None)
            return currentText;

        ed.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{UIMessages.Command.CancelChangeArrowText}");
        return currentText;
    }

    private static void CreateQuickArrow(
        Document doc,
        Point3d tailPoint,
        IReadOnlyList<Point3d> pathPointsFromTail,
        string tailText)
    {
        if (pathPointsFromTail.Count == 0)
            return;

        var normalizedTailText = NormalizeTailText(tailText);
        if (normalizedTailText.Length == 0)
        {
            CreateQuickArrowEntity(doc, tailPoint, pathPointsFromTail, normalizedTailText);
            return;
        }

        CreateQuickArrowEntity(doc, tailPoint, pathPointsFromTail, normalizedTailText);
    }

    private static void CreateQuickArrowEntity(
        Document doc,
        Point3d tailPoint,
        IReadOnlyList<Point3d> pathPointsFromTail,
        string normalizedTailText)
    {
        var hasTailText = normalizedTailText.Length > 0;

        CadDatabaseScope.Write(
            doc,
            (db, tr) =>
            {
                var currentSpace = CadDatabaseScope.OpenCurrentSpaceForWrite(db, tr);
                var ml = new MLeader();
                var appended = false;

                try
                {
                    MLeaderStyleHelper.ApplyPreferredStyleToMLeader(
                        ml,
                        db,
                        UserConfigurationStore.TryGetMLeaderStyleName(PluginCommandIds.QuickArrow),
                        ForcedMLeaderStyleName);
                    ml.ContentType = ContentType.MTextContent;
                    var tailTextLayout = ResolveTailTextLayout(tailPoint, pathPointsFromTail[0]);
                    var leaderVertices = BuildLeaderVerticesArrowToTail(tailPoint, pathPointsFromTail);

                    var leaderIndex = ml.AddLeader();
                    var leaderLineIndex = ml.AddLeaderLine(leaderIndex);
                    InitializeLeaderVertices(ml, leaderLineIndex, leaderVertices);

                    // 使用空白 MText 作为占位，可避开部分环境下 NoneContent 在提交数据库时的状态异常。
                    var displayText = hasTailText ? normalizedTailText : " ";
                    ml.MText = CreateTailTextContent(ml, tr, db, displayText, tailTextLayout.Attachment);
                    MLeaderStyleHelper.ApplyMLeaderStylePropertiesToEntity(ml, tr);
                    ApplyQuickArrowOverrides(ml, tailTextLayout);

                    if (hasTailText)
                    {
                        ApplyTextTailGeometry(ml, leaderLineIndex, leaderVertices, tailPoint);
                    }
                    else
                    {
                        ApplyArrowOnlyGeometry(ml, leaderLineIndex, leaderVertices);
                        TrySetTextLocation(ml, tailPoint);
                        ApplyLeaderVertices(ml, leaderLineIndex, leaderVertices);
                    }

                    currentSpace.AppendEntity(ml);
                    tr.AddNewlyCreatedDBObject(ml, true);
                    appended = true;
                }
                finally
                {
                    if (!appended)
                        ml.Dispose();
                }

                return true;
            },
            requireDocumentLock: true);
    }

    private static MText CreateTailTextContent(
        MLeader ml,
        Transaction tr,
        Database db,
        string tailText,
        AttachmentPoint attachment)
    {
        var mt = new MText();
        mt.SetDatabaseDefaults(db);
        MLeaderStyleHelper.ApplyMLeaderStyleToNewMText(ml, tr, mt);
        mt.Attachment = attachment;
        mt.Contents = EscapeMText(tailText);
        mt.TextHeight = FixedTextHeight;
        return mt;
    }

    private static TailTextLayout ResolveTailTextLayout(Point3d tailPoint, Point3d tailNeighborPoint)
    {
        var delta = tailPoint - tailNeighborPoint;
        var isVertical = Math.Abs(delta.Y) > Math.Abs(delta.X);
        if (isVertical)
        {
            var attachment = delta.Y >= 0
                ? AttachmentPoint.BottomCenter
                : AttachmentPoint.TopCenter;
            return new TailTextLayout(attachment, TextAttachmentDirection.AttachmentVertical);
        }

        var horizontalAttachment = delta.X >= 0
            ? AttachmentPoint.MiddleLeft
            : AttachmentPoint.MiddleRight;
        return new TailTextLayout(horizontalAttachment, TextAttachmentDirection.AttachmentHorizontal);
    }

    private static void ApplyQuickArrowOverrides(MLeader ml, TailTextLayout tailTextLayout)
    {
        TrySetArrowSize(ml, FixedArrowSize);
        TrySetTextHeight(ml, FixedTextHeight);
        TrySetLandingGap(ml, 0.0);
        TrySetDoglegLength(ml, 0.0);
        TrySetExtendLeaderToText(ml, false);
        TrySetTextAttachmentDirection(ml, tailTextLayout.Direction);
        TrySetTextAttachmentType(ml, TextAttachmentType.AttachmentMiddle, LeaderDirectionType.LeftLeader);
        TrySetTextAttachmentType(ml, TextAttachmentType.AttachmentMiddle, LeaderDirectionType.RightLeader);
        TrySetTextAttachmentType(ml, TextAttachmentType.AttachmentMiddleOfTop, LeaderDirectionType.TopLeader);
        TrySetTextAttachmentType(ml, TextAttachmentType.AttachmentMiddleOfBottom, LeaderDirectionType.BottomLeader);
        TrySetMTextAttachment(ml, tailTextLayout.Attachment);
        SetEnableLandingSafe(ml, false);
        SetEnableDoglegSafe(ml, false);
    }

    private static Point3d[] BuildLeaderVerticesArrowToTail(Point3d tailPoint, IReadOnlyList<Point3d> pathPointsFromTail)
    {
        var vertices = new Point3d[pathPointsFromTail.Count + 1];
        var targetIndex = 0;
        for (var i = pathPointsFromTail.Count - 1; i >= 0; i--)
            vertices[targetIndex++] = pathPointsFromTail[i];

        vertices[targetIndex] = tailPoint;
        return vertices;
    }

    private static void InitializeLeaderVertices(MLeader ml, int leaderLineIndex, IReadOnlyList<Point3d> leaderVertices)
    {
        if (leaderVertices.Count == 0)
            throw new ArgumentException("Leader vertices cannot be empty.", nameof(leaderVertices));

        ml.AddFirstVertex(leaderLineIndex, leaderVertices[0]);
        for (var i = 1; i < leaderVertices.Count; i++)
            ml.AddLastVertex(leaderLineIndex, leaderVertices[i]);
    }

    private static void ApplyTextTailGeometry(
        MLeader ml,
        int leaderLineIndex,
        IReadOnlyList<Point3d> leaderVertices,
        Point3d tailPoint)
    {
        ApplyArrowOnlyGeometry(ml, leaderLineIndex, leaderVertices);
        TrySetTextLocation(ml, tailPoint);

        // TextLocation 在部分版本下会重算几何，这里把用户输入的首末点再次压回去。
        ApplyLeaderVertices(ml, leaderLineIndex, leaderVertices);
    }

    private static void ApplyArrowOnlyGeometry(MLeader ml, int leaderLineIndex, IReadOnlyList<Point3d> leaderVertices)
    {
        SetEnableLandingSafe(ml, false);
        SetEnableDoglegSafe(ml, false);
        ApplyLeaderVertices(ml, leaderLineIndex, leaderVertices);
    }

    private static void ApplyLeaderVertices(MLeader ml, int leaderLineIndex, IReadOnlyList<Point3d> leaderVertices)
    {
        for (var i = 0; i < leaderVertices.Count; i++)
            TrySetVertex(ml, leaderLineIndex, i, leaderVertices[i]);
    }

    private static void SetEnableLandingSafe(MLeader ml, bool enabled)
    {
        try
        {
            ml.EnableLanding = enabled;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 EnableLanding 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 EnableLanding 失败（CAD）", ex);
        }
    }

    private static void SetEnableDoglegSafe(MLeader ml, bool enabled)
    {
        try
        {
            ml.EnableDogleg = enabled;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 EnableDogleg 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 EnableDogleg 失败（CAD）", ex);
        }
    }

    private static void TrySetArrowSize(MLeader ml, double arrowSize)
    {
        try
        {
            ml.ArrowSize = arrowSize;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 ArrowSize 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 ArrowSize 失败（CAD）", ex);
        }
    }

    private static void TrySetTextHeight(MLeader ml, double textHeight)
    {
        try
        {
            ml.TextHeight = textHeight;

            var mt = ml.MText;
            if (mt == null)
                return;

            mt.TextHeight = textHeight;
            ml.MText = mt;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 TextHeight 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 TextHeight 失败（CAD）", ex);
        }
    }

    private static void TrySetMTextAttachment(MLeader ml, AttachmentPoint attachment)
    {
        try
        {
            var mt = ml.MText;
            if (mt == null)
                return;

            mt.Attachment = attachment;
            ml.MText = mt;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 MText.Attachment 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 MText.Attachment 失败（CAD）", ex);
        }
    }

    private static void TrySetLandingGap(MLeader ml, double landingGap)
    {
        try
        {
            ml.LandingGap = landingGap;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 LandingGap 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 LandingGap 失败（CAD）", ex);
        }
    }

    private static void TrySetVertex(MLeader ml, int leaderLineIndex, int vertexIndex, Point3d vertex)
    {
        try
        {
            ml.SetVertex(leaderLineIndex, vertexIndex, vertex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 Vertex 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 Vertex 失败（CAD）", ex);
        }
    }

    private static void TrySetDoglegLength(MLeader ml, double doglegLength)
    {
        try
        {
            ml.DoglegLength = doglegLength;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 DoglegLength 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 DoglegLength 失败（CAD）", ex);
        }
    }

    private static void TrySetExtendLeaderToText(MLeader ml, bool enabled)
    {
        try
        {
            ml.ExtendLeaderToText = enabled;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 ExtendLeaderToText 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 ExtendLeaderToText 失败（CAD）", ex);
        }
    }

    private static void TrySetTextAttachmentDirection(MLeader ml, TextAttachmentDirection direction)
    {
        try
        {
            ml.TextAttachmentDirection = direction;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 TextAttachmentDirection 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 TextAttachmentDirection 失败（CAD）", ex);
        }
    }

    private static void TrySetTextAttachmentType(MLeader ml, TextAttachmentType attachmentType, LeaderDirectionType directionType)
    {
        try
        {
            ml.SetTextAttachmentType(attachmentType, directionType);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 TextAttachmentType 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 TextAttachmentType 失败（CAD）", ex);
        }
    }

    private static bool TrySetTextLocation(MLeader ml, Point3d location)
    {
        try
        {
            ml.TextLocation = location;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 TextLocation 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("F_JT 设置 TextLocation 失败（CAD）", ex);
        }

        return false;
    }

    private static string NormalizeTailText(string? tailText) => (tailText ?? "").Trim();

    private static string EscapeMText(string text) =>
        text.Replace("\\", "\\\\")
            .Replace("{", "\\{")
            .Replace("}", "\\}");

    private static string DescribeCurrentText(string text)
    {
        var normalized = NormalizeTailText(text);
        if (normalized.Length == 0)
            return "无";

        normalized = normalized.Replace("\r", " ")
            .Replace("\n", " ");
        if (normalized.Length > 20)
            normalized = normalized.Substring(0, 20) + "...";

        return $"“{normalized}”";
    }

    private enum PointPromptAction
    {
        Cancel,
        Settings,
        Finish,
        Point
    }

    private readonly struct PointPromptResult
    {
        internal PointPromptResult(PointPromptAction action, Point3d point)
        {
            Action = action;
            Point = point;
        }

        internal PointPromptAction Action { get; }

        internal Point3d Point { get; }

        internal static PointPromptResult ForCancel() => new(PointPromptAction.Cancel, Point3d.Origin);

        internal static PointPromptResult ForSettings() => new(PointPromptAction.Settings, Point3d.Origin);

        internal static PointPromptResult ForFinish() => new(PointPromptAction.Finish, Point3d.Origin);

        internal static PointPromptResult FromPoint(Point3d point) => new(PointPromptAction.Point, point);
    }

    private readonly struct TailTextLayout
    {
        internal TailTextLayout(AttachmentPoint attachment, TextAttachmentDirection direction)
        {
            Attachment = attachment;
            Direction = direction;
        }

        internal AttachmentPoint Attachment { get; }

        internal TextAttachmentDirection Direction { get; }
    }
}
