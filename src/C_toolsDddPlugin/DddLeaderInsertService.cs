using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using C_toolsShared;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using C_toolsPlugin;

namespace C_toolsDddPlugin;

/// <summary>文字标注面板与 <see cref="DddCommands"/> 之间的待插入文本传递。</summary>
internal static class DddLeaderInsertService
{
    private static readonly Dictionary<string, string> s_pendingTexts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> s_pendingStyleNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> s_lastInsertedTexts = new(StringComparer.OrdinalIgnoreCase);
    private const double MinimumLeaderPointDistance = 1e-6;

    internal static string? PendingText
    {
        get
        {
            var doc = AcAp.DocumentManager.MdiActiveDocument;
            return doc == null ? null : TakePendingText(doc);
        }
        set
        {
            var doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            SetPendingText(doc, value);
        }
    }

    internal static string? LastInsertedText
    {
        get
        {
            var doc = AcAp.DocumentManager.MdiActiveDocument;
            return doc == null ? null : TryGetLastInsertedText(doc);
        }
    }

    internal static void SetPendingText(Document doc, string? text)
    {
        var value = NormalizeStoredText(text);
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return;

        if (!DddTextContentHelper.HasVisibleText(value))
        {
            s_pendingTexts.Remove(key);
            return;
        }

        s_pendingTexts[key] = value;
    }

    internal static void ClearPendingText(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return;

        s_pendingTexts.Remove(key);
    }

    internal static void ClearDocumentState(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return;

        s_pendingTexts.Remove(key);
        s_pendingStyleNames.Remove(key);
    }

    internal static void SetPendingMLeaderStyle(Document doc, string? styleName)
    {
        var value = (styleName ?? "").Trim();
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return;

        if (value.Length == 0)
        {
            s_pendingStyleNames.Remove(key);
            return;
        }

        s_pendingStyleNames[key] = value;
    }

    internal static void ClearPendingMLeaderStyle(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return;

        s_pendingStyleNames.Remove(key);
    }

    internal static string? TakePendingMLeaderStyle(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return null;
        if (!s_pendingStyleNames.TryGetValue(key, out var value))
            return null;

        s_pendingStyleNames.Remove(key);
        return value;
    }

    internal static string? TryGetLastInsertedText(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return null;

        return s_lastInsertedTexts.TryGetValue(key, out var value) ? value : null;
    }

    internal static string? TakePendingText(Document doc)
    {
        var key = GetDocumentKey(doc);
        if (key.Length == 0)
            return null;
        if (!s_pendingTexts.TryGetValue(key, out var value))
            return null;

        s_pendingTexts.Remove(key);
        return value;
    }

    internal static void RecordLastInsertedText(Document doc, string text)
    {
        var value = NormalizeStoredText(text);
        var key = GetDocumentKey(doc);
        if (key.Length == 0 || !DddTextContentHelper.HasVisibleText(value))
            return;

        s_lastInsertedTexts[key] = value;
    }

    internal static void RecordLastInsertedText(string text)
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        RecordLastInsertedText(doc, text);
    }

    internal static string? TakePendingText()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        return doc == null ? null : TakePendingText(doc);
    }

    internal static void ClearCurrentDocumentState()
    {
        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        ClearDocumentState(doc);
    }

    internal static void BeginInsertLeader(string? text)
    {
        var value = NormalizeStoredText(text);
        if (!DddTextContentHelper.HasVisibleText(value))
            return;

        var doc = AcAp.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;

        SetPendingText(doc, value);
        doc.SendStringToExecute("_." + DddPluginCommandIds.DddLeader + "\n", true, false, false);
    }

    internal static string? ResolveInsertText(Document doc)
    {
        var pending = TakePendingText(doc);
        if (!string.IsNullOrWhiteSpace(pending))
            return pending;

        return TryGetLastInsertedText(doc);
    }

    internal static void RememberInsertedText(Document doc, string text)
    {
        RecordLastInsertedText(doc, text);
    }

    internal static void ClearInsertState(Document doc)
    {
        ClearPendingText(doc);
        ClearPendingMLeaderStyle(doc);
    }

    private static string GetDocumentKey(Document doc)
    {
        try
        {
            return doc.Database.FingerprintGuid.ToString();
        }
        catch
        {
            return doc.Name ?? string.Empty;
        }
    }

    private static string NormalizeStoredText(string? text)
    {
        return DddTextContentHelper.NormalizeLineEndings(text);
    }

    internal static string ToMTextSafe(string s)
    {
        return DddTextContentHelper.ToMTextContents(s);
    }

    internal static void RestoreLeftRightTextAttachmentOverrides(
        MLeader ml,
        TextAttachmentType styleLeft,
        TextAttachmentType styleRight)
    {
        try
        {
            ml.SetTextAttachmentType(styleLeft, LeaderDirectionType.LeftLeader);
            ml.SetTextAttachmentType(styleRight, LeaderDirectionType.RightLeader);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("MLeader 恢复左/右连接位置", ex);
        }
    }

    internal static void TryCaptureStyleTextAttachments(
        MLeader ml,
        out TextAttachmentType styleLeft,
        out TextAttachmentType styleRight)
    {
        styleLeft = TextAttachmentType.AttachmentMiddle;
        styleRight = TextAttachmentType.AttachmentMiddle;
        try
        {
            styleLeft = ml.GetTextAttachmentType(LeaderDirectionType.LeftLeader);
            styleRight = ml.GetTextAttachmentType(LeaderDirectionType.RightLeader);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("MLeader 读取左/右连接位置", ex);
        }
    }

    private static double GetEffectiveTextHeight(MLeader ml, Transaction? tr)
    {
        if (tr != null && MLeaderStyleHelper.TryGetStyleTextHeightFromMLeader(ml, tr, out var styleHeight))
            return styleHeight;

        try
        {
            if (ml.TextHeight > 1e-9)
                return ml.TextHeight;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader 字高失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader 字高失败（CAD）", ex);
        }

        return 2.5;
    }

    private static bool IsLandingEnabled(MLeader ml)
    {
        try
        {
            return ml.EnableLanding;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader Landing 开关失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader Landing 开关失败（CAD）", ex);
        }

        return true;
    }

    private static bool IsDoglegEnabled(MLeader ml)
    {
        try
        {
            return ml.EnableDogleg;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader Dogleg 开关失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader Dogleg 开关失败（CAD）", ex);
        }

        return true;
    }

    private static double GetEffectiveDoglegLength(MLeader ml)
    {
        try
        {
            var value = ml.DoglegLength;
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 1e-9)
                return value;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader DoglegLength 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader DoglegLength 失败（CAD）", ex);
        }

        return 0.0;
    }

    private static double GetConnectionOffsetFromLandingPoint(MLeader ml)
    {
        if (!IsLandingEnabled(ml) || !IsDoglegEnabled(ml))
            return 0.0;

        return GetEffectiveDoglegLength(ml);
    }

    private static double GetEffectiveArrowSize(MLeader ml)
    {
        try
        {
            var value = ml.ArrowSize;
            if (!double.IsNaN(value) && !double.IsInfinity(value) && value > 1e-9)
                return value;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader ArrowSize 失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeader ArrowSize 失败（CAD）", ex);
        }

        return 0.0;
    }

    private static double GetLandingSideDeadZone(MLeader ml)
    {
        var textHeight = GetEffectiveTextHeight(ml, null);
        var arrowSize = GetEffectiveArrowSize(ml);
        var doglegLength = GetEffectiveDoglegLength(ml);

        return Math.Max(
            Math.Max(arrowSize, textHeight * 0.75),
            doglegLength * 0.2);
    }

    private static Point3d EnsureUsableLandingPoint(
        MLeader ml,
        Point3d arrowPoint,
        Point3d landingPoint,
        int landingSideSign)
    {
        if (!IsFinitePoint(arrowPoint) || !IsFinitePoint(landingPoint))
            return landingPoint;

        if (arrowPoint.DistanceTo(landingPoint) > MinimumLeaderPointDistance)
            return landingPoint;

        var sign = landingSideSign < 0 ? -1 : 1;
        var fallbackDistance = Math.Max(
            Math.Max(GetEffectiveDoglegLength(ml), GetEffectiveArrowSize(ml)),
            GetEffectiveTextHeight(ml, null) * 0.25);

        if (double.IsNaN(fallbackDistance)
            || double.IsInfinity(fallbackDistance)
            || fallbackDistance <= MinimumLeaderPointDistance)
        {
            fallbackDistance = 1.0;
        }

        return new Point3d(
            arrowPoint.X + (sign * fallbackDistance),
            arrowPoint.Y,
            arrowPoint.Z);
    }

    private static bool IsFinitePoint(Point3d point) =>
        IsFinite(point.X) && IsFinite(point.Y) && IsFinite(point.Z);

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    internal static int ResolveLandingSideSign(
        MLeader ml,
        Point3d arrowPoint,
        Point3d landingPoint,
        int fallbackSign)
    {
        var sign = fallbackSign < 0 ? -1 : 1;
        var deltaX = landingPoint.X - arrowPoint.X;
        var deadZone = GetLandingSideDeadZone(ml);

        if (deltaX <= -deadZone)
            return -1;

        if (deltaX >= deadZone)
            return 1;

        return sign;
    }

    internal static Point3d ComputeTextLocationFromLandingPoint(
        MLeader ml,
        Point3d arrowPoint,
        Point3d landingPoint,
        int landingSideSign)
    {
        var sign = landingSideSign < 0 ? -1 : 1;
        var connectionOffset = GetConnectionOffsetFromLandingPoint(ml);
        var targetConnection = new Point3d(
            landingPoint.X + (sign * connectionOffset),
            landingPoint.Y,
            landingPoint.Z);
        var provisional = new Point3d(
            landingPoint.X + (sign * connectionOffset),
            landingPoint.Y,
            landingPoint.Z);

        var direction = new Vector3d(sign, 0, 0);
        var resolved = provisional;

        for (var i = 0; i < 2; i++)
        {
            ml.TextLocation = resolved;

            Point3d actualConnection;
            try
            {
                actualConnection = ml.ConnectionPoint(direction, ml.TextAttachmentDirection);
            }
            catch
            {
                actualConnection = ml.ConnectionPoint(direction);
            }

            var delta = targetConnection - actualConnection;
            resolved = new Point3d(
                resolved.X + delta.X,
                resolved.Y + delta.Y,
                resolved.Z + delta.Z);
        }

        return resolved;
    }

    internal static void ApplyLandingGeometry(
        MLeader ml,
        int leaderIndex,
        int leaderLineIndex,
        Point3d arrowPoint,
        Point3d landingPoint,
        int landingSideSign)
    {
        var sign = landingSideSign < 0 ? -1 : 1;
        landingPoint = EnsureUsableLandingPoint(ml, arrowPoint, landingPoint, sign);
        var doglegLength = GetEffectiveDoglegLength(ml);

        if (IsLandingEnabled(ml) && IsDoglegEnabled(ml))
        {
            try
            {
                ml.SetDogleg(leaderIndex, new Vector3d(sign, 0, 0));
                if (doglegLength > 1e-9)
                    ml.SetDoglegLength(leaderIndex, doglegLength);
            }
            catch (InvalidOperationException ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 设置 Dogleg", ex);
            }
        }

        ml.SetFirstVertex(leaderLineIndex, arrowPoint);
        ml.SetLastVertex(leaderLineIndex, landingPoint);

        var resolvedTextLocation = ComputeTextLocationFromLandingPoint(ml, arrowPoint, landingPoint, sign);
        ml.TextLocation = resolvedTextLocation;

        ml.SetFirstVertex(leaderLineIndex, arrowPoint);
        ml.SetLastVertex(leaderLineIndex, landingPoint);
    }

    internal static MLeader CreatePreviewMLeader(Database db, string mtextContents, Point3d ptArrow, out int leaderIndex, out int leaderLineIndex)
    {
        var ml = new MLeader();
        MLeaderStyleHelper.ApplyCurrentStyleToMLeader(ml, db);
        ml.ContentType = ContentType.MTextContent;

        using (var styleTr = db.TransactionManager.StartTransaction())
        {
            MLeaderStyleHelper.EnsureMLeaderStyleAllowsTwoPoints(ml, styleTr);
            styleTr.Commit();
        }

        leaderIndex = ml.AddLeader();
        leaderLineIndex = ml.AddLeaderLine(leaderIndex);
        var initialLandingPoint = EnsureUsableLandingPoint(ml, ptArrow, ptArrow, 1);
        ml.AddFirstVertex(leaderLineIndex, ptArrow);
        ml.AddLastVertex(leaderLineIndex, initialLandingPoint);

        using (var tr = db.TransactionManager.StartTransaction())
        {
            var settings = MLeaderToolSettingsStore.LoadOrDefault();
            var mt = new MText();
            mt.SetDatabaseDefaults(db);
            MLeaderStyleHelper.ApplyMLeaderStyleToNewMText(ml, tr, mt);
            mt.Contents = mtextContents;
            mt.TextHeight = GetEffectiveTextHeight(ml, tr);
            ml.MText = mt;

            MLeaderStyleHelper.ApplyMLeaderStylePropertiesToEntity(ml, tr);
            MLeaderPluginAppearanceApplier.ApplyToMLeader(ml, tr, db, settings);
            tr.Commit();
        }

        ApplyLandingGeometry(ml, leaderIndex, leaderLineIndex, ptArrow, ptArrow, 1);
        return ml;
    }

    private static MText CreateConfiguredPlainMText(Database db, Transaction tr, string mtextContents, Point3d location)
    {
        var settings = MLeaderToolSettingsStore.LoadOrDefault();
        var ml = new MLeader();
        MLeaderStyleHelper.ApplyCurrentStyleToMLeader(ml, db);
        ml.ContentType = ContentType.MTextContent;

        var mt = new MText();
        mt.SetDatabaseDefaults(db);
        MLeaderStyleHelper.ApplyMLeaderStyleToNewMText(ml, tr, mt);
        mt.Contents = mtextContents;
        ml.MText = mt;

        MLeaderStyleHelper.ApplyMLeaderStylePropertiesToEntity(ml, tr);
        MLeaderPluginAppearanceApplier.ApplyToMLeader(ml, tr, db, settings);

        var resolved = ml.MText ?? mt;
        var plainText = new MText();
        plainText.SetDatabaseDefaults(db);
        plainText.CopyFrom(resolved);
        plainText.Contents = mtextContents;
        plainText.Location = location;
        return plainText;
    }

    internal static void CreateMLeader(Document doc, string mtextContents, Point3d ptArrow, Point3d ptLanding, int landingSideSign)
    {
        var db = doc.Database;
        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var settings = MLeaderToolSettingsStore.LoadOrDefault();
            var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

            var ml = new MLeader();
            MLeaderStyleHelper.ApplyCurrentStyleToMLeader(ml, doc);
            ml.ContentType = ContentType.MTextContent;
            MLeaderStyleHelper.EnsureMLeaderStyleAllowsTwoPoints(ml, tr);

            var leaderIndex = ml.AddLeader();
            var leaderLineIndex = ml.AddLeaderLine(leaderIndex);
            var initialLandingPoint = EnsureUsableLandingPoint(ml, ptArrow, ptLanding, landingSideSign);
            ml.AddFirstVertex(leaderLineIndex, ptArrow);
            ml.AddLastVertex(leaderLineIndex, initialLandingPoint);

            var mt = new MText();
            mt.SetDatabaseDefaults(db);
            MLeaderStyleHelper.ApplyMLeaderStyleToNewMText(ml, tr, mt);
            mt.Contents = mtextContents;
            mt.TextHeight = GetEffectiveTextHeight(ml, tr);
            ml.MText = mt;

            MLeaderStyleHelper.ApplyMLeaderStylePropertiesToEntity(ml, tr);
            MLeaderPluginAppearanceApplier.ApplyToMLeader(ml, tr, db, settings);
            ApplyLandingGeometry(ml, leaderIndex, leaderLineIndex, ptArrow, ptLanding, landingSideSign);

            ms.AppendEntity(ml);
            tr.AddNewlyCreatedDBObject(ml, true);
            tr.Commit();
        }
    }

    internal static void CreatePlainText(Document doc, string rawText, Point3d location)
    {
        var db = doc.Database;
        var mtextContents = ToMTextSafe(rawText);

        using (doc.LockDocument())
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var ms = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            var mt = CreateConfiguredPlainMText(db, tr, mtextContents, location);
            ms.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
            tr.Commit();
        }
    }

    internal static void RunInteractiveLeader(Document doc)
    {
        var resolvedText = ResolveInsertText(doc);
        if (string.IsNullOrWhiteSpace(resolvedText))
        {
            doc.Editor.WriteMessage("\nC_TOOL：无待插入文字且无上次插入内容。");
            return;
        }

        var insertText = resolvedText!;
        var ed = doc.Editor;
        var p1 = ed.GetPoint("\nC_TOOL：第一点：指定引线箭头位置（单击确定，箭头与引线起点固定）: ");
        if (p1.Status != PromptStatus.OK)
            return;

        var db = doc.Database;
        var mtextContents = ToMTextSafe(insertText);
        var preview = CreatePreviewMLeader(db, mtextContents, p1.Value, out var leaderIndex, out var leaderLineIndex);
        var jig = new DddLeaderTextJig(preview, p1.Value, leaderIndex, leaderLineIndex);
        var dragRes = ed.Drag(jig);
        if (dragRes.Status != PromptStatus.OK)
        {
            preview.Dispose();
            return;
        }

        var p2 = jig.LandingPoint;
        var landingSideSign = jig.LandingSideSign;
        preview.Dispose();

        try
        {
            CreateMLeader(doc, mtextContents, p1.Value, p2, landingSideSign);
            RememberInsertedText(doc, insertText);
            doc.Editor.WriteMessage("\nC_TOOL：已插入多重引线。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("插入多重引线（无效操作）", ex);
            doc.Editor.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{string.Format(UIMessages.Leader.SetLeaderFailed, ex.Message)}");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("插入多重引线", ex);
            doc.Editor.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{string.Format(UIMessages.Leader.SetLeaderFailed, ex.Message)}");
        }
    }

    internal static void RunInteractiveText(Document doc)
    {
        var resolvedText = ResolveInsertText(doc);
        if (string.IsNullOrWhiteSpace(resolvedText))
        {
            doc.Editor.WriteMessage("\nC_TOOL：无待插入文字且无上次插入内容。");
            return;
        }

        var insertText = resolvedText!;
        var ed = doc.Editor;
        var point = ed.GetPoint("\nC_TOOL：指定文字插入点: ");
        if (point.Status != PromptStatus.OK)
            return;

        try
        {
            CreatePlainText(doc, insertText, point.Value);
            RememberInsertedText(doc, insertText);
            doc.Editor.WriteMessage("\nC_TOOL：已插入文字。");
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("插入纯文字（无效操作）", ex);
            doc.Editor.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{string.Format(UIMessages.Leader.InsertTextFailed, ex.Message)}");
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("插入纯文字", ex);
            doc.Editor.WriteMessage($"\n{UIMessages.Prefix_C_TOOL}{string.Format(UIMessages.Leader.InsertTextFailed, ex.Message)}");
        }
    }
}
