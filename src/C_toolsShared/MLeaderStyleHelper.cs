using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace C_toolsShared;

/// <summary>当前 CMLEADERSTYLE 对应 <see cref="MLeaderStyle"/> 的只读展示数据。</summary>
public sealed class MLeaderStyleDisplaySnapshot
{
    public string StyleName { get; init; } = "";

    public string LeaderLineColorUi { get; init; } = "";

    public string BlockColorUi { get; init; } = "";

    public string ArrowBlockName { get; init; } = "";

    public string ArrowSizeText { get; init; } = "";

    public string TextHeightText { get; init; } = "";

    public string TextStyleName { get; init; } = "";

    public string TextColorUi { get; init; } = "";

    public string? ErrorNote { get; init; }
}

/// <summary>当前图形多重引线样式：与系统变量 CMLEADERSTYLE、字典中的 <see cref="MLeaderStyle"/> 同步。</summary>
public static class MLeaderStyleHelper
{
    public const string SysVarCurrentMLeaderStyle = "CMLEADERSTYLE";

    /// <summary>
    /// 当前图纸的 CMLEADERSTYLE。若传入的 <paramref name="doc"/> 非当前工作库，则临时切换 <see cref="HostApplicationServices.WorkingDatabase"/> 再读系统变量。
    /// </summary>
    public static string? TryGetCurrentStyleName(Document? doc)
    {
        if (TryRunWithWorkingDatabase(
                doc,
                ReadCurrentStyleNameCore,
                "读取 CMLEADERSTYLE（WorkingDatabase）",
                out var workingDatabaseName) &&
            !string.IsNullOrWhiteSpace(workingDatabaseName))
        {
            return workingDatabaseName;
        }

        try
        {
            return ReadCurrentStyleNameCore();
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 CMLEADERSTYLE（Application）", ex);
            return null;
        }
    }

    public static string? TryGetCurrentStyleName() => TryGetCurrentStyleName(null);

    public static bool TrySetCurrentStyleName(Document? doc, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var normalizedName = name.Trim();

        if (TryRunWithWorkingDatabase(
                doc,
                () =>
                {
                    AcAp.SetSystemVariable(SysVarCurrentMLeaderStyle, normalizedName);
                    return true;
                },
                "设置 CMLEADERSTYLE（WorkingDatabase）",
                out var workingDatabaseApplied) &&
            workingDatabaseApplied)
        {
            return true;
        }

        try
        {
            AcAp.SetSystemVariable(SysVarCurrentMLeaderStyle, normalizedName);
            return true;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("设置 CMLEADERSTYLE（Application）", ex);
            return false;
        }
    }

    public static bool TrySetCurrentStyleName(string name) => TrySetCurrentStyleName(null, name);

    public static List<string> GetStyleNames(Database db)
    {
        try
        {
            return CadDatabaseScope.Read(db, (database, tr) => GetStyleNames(tr, database));
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("枚举多重引线样式", ex);
            return new List<string>();
        }
    }

    public static List<string> GetStyleNames(Transaction tr, Database db)
    {
        var list = new List<string>();
        var dict = CadDatabaseScope.OpenAs<DBDictionary>(tr, db.MLeaderStyleDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in dict)
        {
            if (CadDatabaseScope.TryOpenAs<MLeaderStyle>(tr, entry.Value, OpenMode.ForRead, out var style) &&
                style != null &&
                !string.IsNullOrWhiteSpace(style.Name))
            {
                list.Add(style.Name);
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static string? ReadCurrentStyleNameCore()
    {
        var value = AcAp.GetSystemVariable(SysVarCurrentMLeaderStyle);
        var text = value?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static bool TryRunWithWorkingDatabase<T>(
        Document? doc,
        Func<T> action,
        string operationName,
        out T result)
    {
        if (action == null)
            throw new ArgumentNullException(nameof(action));

        result = default!;
        if (doc == null)
            return false;

        var database = doc.Database;
        if (ReferenceEquals(HostApplicationServices.WorkingDatabase, database))
            return false;

        try
        {
            result = CadWorkingDatabaseScope.Run(database, action);
            return true;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal(operationName, ex);
            return false;
        }
    }

    /// <summary>须在 CAD 应用程序上下文 + 有效 <paramref name="tr"/> 内调用。</summary>
    public static MLeaderStyleDisplaySnapshot TryReadCurrentStyleSnapshot(Transaction tr, Database db)
    {
        Document? docForVar = null;
        try
        {
            var active = AcAp.DocumentManager.MdiActiveDocument;
            if (active?.Database == db)
                docForVar = active;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取活动文档失败（无效操作）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取活动文档失败（CAD）", ex);
        }

        var name = TryGetCurrentStyleName(docForVar);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new MLeaderStyleDisplaySnapshot
            {
                ErrorNote = "无法读取 CMLEADERSTYLE。"
            };
        }

        var trimmed = name!.Trim();
        if (!TryGetMLeaderStyleObjectId(tr, db, trimmed, out var oid) || oid.IsNull)
        {
            return new MLeaderStyleDisplaySnapshot
            {
                StyleName = trimmed,
                ErrorNote = "当前图形中未找到多重引线样式「" + trimmed + "」。"
            };
        }

        var ms = (MLeaderStyle)tr.GetObject(oid, OpenMode.ForRead);
        var textStyle = "";
        if (!ms.TextStyleId.IsNull && tr.GetObject(ms.TextStyleId, OpenMode.ForRead) is TextStyleTableRecord tsr)
            textStyle = tsr.Name ?? "";
        var arrowBlock = "";
        if (!ms.ArrowSymbolId.IsNull && tr.GetObject(ms.ArrowSymbolId, OpenMode.ForRead) is BlockTableRecord btr)
            arrowBlock = btr.Name ?? "";

        return new MLeaderStyleDisplaySnapshot
        {
            StyleName = ms.Name ?? trimmed,
            LeaderLineColorUi = MLeaderToolCadColor.ToUiString(ms.LeaderLineColor),
            BlockColorUi = MLeaderToolCadColor.ToUiString(ms.BlockColor),
            ArrowBlockName = arrowBlock,
            ArrowSizeText = ms.ArrowSize.ToString(CultureInfo.InvariantCulture),
            TextHeightText = ms.TextHeight.ToString(CultureInfo.InvariantCulture),
            TextStyleName = textStyle,
            TextColorUi = MLeaderToolCadColor.ToUiString(ms.TextColor)
        };
    }

    /// <summary>按名称解析当前图纸中的多重引线样式对象 ID。</summary>
    public static bool TryGetMLeaderStyleObjectId(Database db, string styleName, out ObjectId styleId)
    {
        styleId = ObjectId.Null;
        if (string.IsNullOrWhiteSpace(styleName))
            return false;
        try
        {
            styleId = CadDatabaseScope.Read(
                db,
                (database, tr) =>
                {
                    _ = TryGetMLeaderStyleObjectId(tr, database, styleName, out var resolvedStyleId);
                    return resolvedStyleId;
                });

            return !styleId.IsNull;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析多重引线样式 ObjectId", ex);
        }

        return false;
    }

    public static bool TryGetMLeaderStyleObjectId(Transaction tr, Database db, string styleName, out ObjectId styleId)
    {
        styleId = ObjectId.Null;
        if (string.IsNullOrWhiteSpace(styleName))
            return false;
        try
        {
            var trimmedStyleName = styleName.Trim();
            var dict = CadDatabaseScope.OpenAs<DBDictionary>(tr, db.MLeaderStyleDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                if (CadDatabaseScope.TryOpenAs<MLeaderStyle>(tr, entry.Value, OpenMode.ForRead, out var style) &&
                    style != null &&
                    string.Equals(style.Name, trimmedStyleName, StringComparison.OrdinalIgnoreCase))
                {
                    styleId = entry.Value;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析多重引线样式 ObjectId（事务内）", ex);
        }

        return false;
    }

    /// <summary>
    /// 将当前 CMLEADERSTYLE 对应样式复制为 <paramref name="newStyleName"/> 并写入字典，
    /// 事务提交后把当前样式切换为新名称。
    /// </summary>
    /// <remarks>
    /// 从 WPF 浮窗写库时必须 <see cref="Document.LockDocument"/>，否则事务可能无法真正提交，
    /// 样式管理器中也看不到新样式。
    /// </remarks>
    public static bool TryCreateDuplicateStyleFromCurrent(Document doc, string newStyleName, out string error)
    {
        error = "";
        newStyleName = (newStyleName ?? "").Trim();
        if (newStyleName.Length == 0)
        {
            error = "名称不能为空。";
            return false;
        }
        try
        {
            var createError = "";
            var created = CadDatabaseScope.Write(
                doc,
                (database, tr) =>
                {
                    if (TryGetMLeaderStyleObjectId(tr, database, newStyleName, out var existing) && !existing.IsNull)
                    {
                        createError = "图形中已存在同名多重引线样式「" + newStyleName + "」。";
                        return false;
                    }

                    var curName = TryGetCurrentStyleName(doc)?.Trim() ?? "";
                    if (curName.Length == 0)
                        curName = "Standard";

                    if (!TryGetMLeaderStyleObjectId(tr, database, curName, out var sourceId) || sourceId.IsNull)
                    {
                        var names = GetStyleNames(tr, database);
                        if (names.Count == 0)
                        {
                            createError = "图纸中未找到多重引线样式，无法复制。";
                            return false;
                        }

                        curName = names[0];
                        if (!TryGetMLeaderStyleObjectId(tr, database, curName, out sourceId) || sourceId.IsNull)
                        {
                            createError = "无法解析用作模板的多重引线样式。";
                            return false;
                        }
                    }

                    _ = CadDatabaseScope.OpenAs<DBDictionary>(tr, database.MLeaderStyleDictionaryId, OpenMode.ForWrite);

                    var source = CadDatabaseScope.OpenAs<MLeaderStyle>(tr, sourceId, OpenMode.ForRead);
                    MLeaderStyle duplicatedStyle;
                    try
                    {
                        duplicatedStyle = (MLeaderStyle)source.Clone();
                    }
                    catch (Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("MLeaderStyle.Clone 回退为 CopyFrom", ex);
                        duplicatedStyle = new MLeaderStyle();
                        duplicatedStyle.CopyFrom(source);
                    }

                    duplicatedStyle.PostMLeaderStyleToDb(database, newStyleName);
                    tr.AddNewlyCreatedDBObject(duplicatedStyle, true);
                    return true;
                },
                requireDocumentLock: true);

            if (!created)
            {
                error = createError;
                return false;
            }

            if (!TrySetCurrentStyleName(newStyleName))
            {
                error = "已创建样式「" + newStyleName + "」，但无法将 CMLEADERSTYLE 切换为该名称。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("复制并新建多重引线样式", ex);
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 与当前 <see cref="SysVarCurrentMLeaderStyle"/> 一致：先 <see cref="MLeader.SetDatabaseDefaults"/>，
    /// 再显式指定样式（保证与 CMLEADERSTYLE 同步，供插件插入实体使用）。
    /// </summary>
    public static void ApplyCurrentStyleToMLeader(MLeader ml, Document doc)
    {
        var db = doc.Database;
        ml.SetDatabaseDefaults(db);
        var n = TryGetCurrentStyleName(doc);
        if (string.IsNullOrWhiteSpace(n))
            return;
        var name = n!.Trim();
        if (TryGetMLeaderStyleObjectId(db, name, out var oid) && !oid.IsNull)
            ml.MLeaderStyle = oid;
    }

    /// <summary>
    /// 兼容仅持有 <see cref="Database"/> 的调用方：按当前 CMLEADERSTYLE 将样式挂到实体。
    /// </summary>
    public static void ApplyCurrentStyleToMLeader(MLeader ml, Database db)
    {
        ml.SetDatabaseDefaults(db);
        var n = TryGetCurrentStyleName();
        if (string.IsNullOrWhiteSpace(n))
            return;
        var name = n!.Trim();
        if (TryGetMLeaderStyleObjectId(db, name, out var oid) && !oid.IsNull)
            ml.MLeaderStyle = oid;
    }

    public static void EnsureMLeaderStyleAllowsTwoPoints(MLeader ml, Transaction tr)
    {
        if (ml == null || tr == null || ml.MLeaderStyle.IsNull)
            return;

        try
        {
            var ms = (MLeaderStyle)tr.GetObject(ml.MLeaderStyle, OpenMode.ForWrite);
            if (ms.MaxLeaderSegmentsPoints > 0 && ms.MaxLeaderSegmentsPoints < 2)
                ms.MaxLeaderSegmentsPoints = 2;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("MLeaderStyle 修正最大引线点数", ex);
        }
    }

    /// <summary>
    /// 为新实体优先挂接指定样式；若图纸中不存在该名称，则稳定回退到按名称排序后的首个样式，
    /// 避免插入逻辑继续跟随当前 CMLEADERSTYLE。
    /// </summary>
    public static void ApplyPreferredStyleToMLeader(MLeader ml, Database db, params string?[] preferredStyleNames)
    {
        ml.SetDatabaseDefaults(db);

        if (preferredStyleNames != null)
        {
            foreach (var preferredStyleName in preferredStyleNames)
            {
                var normalized = (preferredStyleName ?? string.Empty).Trim();
                if (normalized.Length == 0)
                    continue;

                if (TryGetMLeaderStyleObjectId(db, normalized, out var preferredStyleId) && !preferredStyleId.IsNull)
                {
                    ml.MLeaderStyle = preferredStyleId;
                    return;
                }
            }
        }

        var styleNames = GetStyleNames(db);
        foreach (var styleName in styleNames)
        {
            if (TryGetMLeaderStyleObjectId(db, styleName, out var fallbackStyleId) && !fallbackStyleId.IsNull)
            {
                ml.MLeaderStyle = fallbackStyleId;
                return;
            }
        }
    }

    /// <summary>
    /// 将字典中 <see cref="MLeaderStyle"/> 的外观参数写入实体（仅挂 ObjectId 时部分环境不会自动同步到显示）。
    /// </summary>
    public static void ApplyMLeaderStylePropertiesToEntity(MLeader ml, Transaction tr)
    {
        if (ml.MLeaderStyle.IsNull)
            return;
        try
        {
            var ms = (MLeaderStyle)tr.GetObject(ml.MLeaderStyle, OpenMode.ForRead);
            try
            {
                if (!ms.TextStyleId.IsNull)
                    ml.TextStyleId = ms.TextStyleId;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 TextStyleId", ex);
            }

            try
            {
                ml.TextHeight = ms.TextHeight;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 TextHeight", ex);
            }

            try
            {
                ml.ArrowSize = ms.ArrowSize;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 ArrowSize", ex);
            }

            try
            {
                if (!ms.ArrowSymbolId.IsNull)
                    ml.ArrowSymbolId = ms.ArrowSymbolId;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 ArrowSymbolId", ex);
            }

            try
            {
                ml.LeaderLineColor = ms.LeaderLineColor;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 LeaderLineColor", ex);
            }

            try
            {
                ml.BlockColor = ms.BlockColor;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 BlockColor", ex);
            }

            try
            {
                ml.TextColor = ms.TextColor;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 TextColor", ex);
            }

            try
            {
                ml.TextAttachmentDirection = ms.TextAttachmentDirection;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 TextAttachmentDirection", ex);
            }

            try
            {
                ml.ExtendLeaderToText = ms.ExtendLeaderToText;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 ExtendLeaderToText", ex);
            }

            try
            {
                ml.EnableLanding = ms.EnableLanding;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 EnableLanding", ex);
            }

            try
            {
                ml.EnableDogleg = ms.EnableDogleg;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 EnableDogleg", ex);
            }

            try
            {
                ml.DoglegLength = ms.DoglegLength;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 DoglegLength", ex);
            }

            try
            {
                ml.LandingGap = ms.LandingGap;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步 LandingGap", ex);
            }

            // 将样式字典中的「连接位置-左/右」写入实体，否则实体上可能仍是默认值，MText 与着陆几何会错位。
            try
            {
                var left = ms.GetTextAttachmentType(LeaderDirectionType.LeftLeader);
                ml.SetTextAttachmentType(left, LeaderDirectionType.LeftLeader);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步连接位置（左）", ex);
            }

            try
            {
                var right = ms.GetTextAttachmentType(LeaderDirectionType.RightLeader);
                ml.SetTextAttachmentType(right, LeaderDirectionType.RightLeader);
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MLeader 同步连接位置（右）", ex);
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ApplyMLeaderStylePropertiesToEntity", ex);
        }
    }

    /// <summary>新建并挂接前的 <see cref="MText"/> 使用多重引线样式中的文字设置，而非当前 TEXTSTYLE/TEXTSIZE。</summary>
    public static void ApplyMLeaderStyleToNewMText(MLeader ml, Transaction tr, MText mt)
    {
        if (ml.MLeaderStyle.IsNull)
            return;
        try
        {
            var ms = (MLeaderStyle)tr.GetObject(ml.MLeaderStyle, OpenMode.ForRead);
            try
            {
                if (!ms.TextStyleId.IsNull)
                    mt.TextStyleId = ms.TextStyleId;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MText 同步 TextStyleId（MLeaderStyle）", ex);
            }

            try
            {
                if (ms.TextHeight > 1e-9)
                    mt.TextHeight = ms.TextHeight;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MText 同步 TextHeight（MLeaderStyle）", ex);
            }

            try
            {
                mt.Color = ms.TextColor;
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MText 同步 TextColor（MLeaderStyle）", ex);
            }

            try
            {
                var def = ms.DefaultMText;
                if (def != null)
                {
                    try
                    {
                        mt.Attachment = def.Attachment;
                    }
                    catch (InvalidOperationException ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("MText 同步 Attachment（无效操作）", ex);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("MText 同步 Attachment（CAD）", ex);
                    }

                    try
                    {
                        mt.LineSpacingFactor = def.LineSpacingFactor;
                    }
                    catch (InvalidOperationException ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("MText 同步 LineSpacingFactor（无效操作）", ex);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("MText 同步 LineSpacingFactor（CAD）", ex);
                    }

                    try
                    {
                        mt.LineSpacingStyle = def.LineSpacingStyle;
                    }
                    catch (InvalidOperationException ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("MText 同步 LineSpacingStyle（无效操作）", ex);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        C_toolsDiagnostics.LogNonFatal("MText 同步 LineSpacingStyle（CAD）", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                C_toolsDiagnostics.LogNonFatal("MText 同步 DefaultMText", ex);
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("ApplyMLeaderStyleToNewMText", ex);
        }
    }

    /// <summary>读取已挂 <see cref="MLeader.MLeaderStyle"/> 上的字高（与多重引线样式管理器一致）。</summary>
    public static bool TryGetStyleTextHeightFromMLeader(MLeader ml, Transaction tr, out double height)
    {
        height = 0;
        if (ml == null || tr == null || ml.MLeaderStyle.IsNull)
            return false;
        try
        {
            var ms = (MLeaderStyle)tr.GetObject(ml.MLeaderStyle, OpenMode.ForRead);
            height = ms.TextHeight;
            return !double.IsNaN(height) && !double.IsInfinity(height) && height > 1e-9;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("读取 MLeaderStyle 字高", ex);
            return false;
        }
    }
}
