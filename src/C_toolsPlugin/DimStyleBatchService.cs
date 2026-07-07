using System.Globalization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsPlugin;

/// <summary>从当前图纸枚举标注样式分组，并批量写入 <see cref="DimStyleTableRecord"/>。</summary>
internal static class DimStyleBatchService
{
    internal static List<DimStyleGroupInfo> ListGroups(Transaction tr, Database db)
    {
        var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId id in dst)
        {
            var r = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            var name = r.Name ?? "";
            if (name.Length == 0)
                continue;
            var key = GetGroupPrefix(name);
            if (!map.TryGetValue(key, out var list))
            {
                list = new List<string>();
                map[key] = list;
            }

            list.Add(name);
        }

        foreach (var kv in map)
            kv.Value.Sort(StringComparer.OrdinalIgnoreCase);

        return map.OrderBy(kv => GetSortBasePrefix(kv.Key), StringComparer.OrdinalIgnoreCase)
            .ThenBy(kv => HasInnerGroupSuffix(kv.Key) ? 1 : 0)
            .Select(kv => new DimStyleGroupInfo(kv.Key, kv.Value))
            .ToList();
    }

    private static string GetGroupPrefix(string styleName)
    {
        var prefix = styleName.Length >= 2 ? styleName.Substring(0, 2) : styleName.Substring(0, 1);
        return HasInnerSuffix(styleName) ? prefix + "内" : prefix;
    }

    private static bool HasInnerSuffix(string styleName)
    {
        var firstDash = styleName.IndexOf('-');
        if (firstDash < 0)
            return styleName.EndsWith("内", StringComparison.Ordinal);

        if (firstDash >= styleName.Length - 1)
            return false;

        var secondDash = styleName.IndexOf('-', firstDash + 1);
        var segmentEnd = secondDash >= 0 ? secondDash : styleName.Length;
        return segmentEnd > firstDash + 1 && styleName[segmentEnd - 1] == '内';
    }

    private static string GetSortBasePrefix(string groupPrefix)
    {
        return HasInnerGroupSuffix(groupPrefix)
            ? groupPrefix.Substring(0, groupPrefix.Length - 1)
            : groupPrefix;
    }

    private static bool HasInnerGroupSuffix(string groupPrefix)
    {
        return groupPrefix.Length > 1 && groupPrefix[^1] == '内';
    }

    /// <summary>块表中非匿名块名，供标注箭头块下拉。</summary>
    internal static List<string> ListArrowBlockNames(Transaction tr, Database db)
    {
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var list = new List<string>();
        foreach (ObjectId id in bt)
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            var name = btr.Name ?? "";
            if (name.Length == 0)
                continue;
            var c0 = name[0];
            if (c0 == '*' || c0 == '|')
                continue;
            list.Add(name);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>文字样式表名，供 DIMTXSTY 下拉。</summary>
    internal static List<string> ListTextStyleNames(Transaction tr, Database db)
    {
        var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        var list = new List<string>();
        foreach (ObjectId id in tst)
        {
            var tsr = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            var name = tsr.Name ?? "";
            if (name.Length == 0)
                continue;
            list.Add(name);
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    internal static bool TryResolvePreferredStyleName(
        Transaction tr,
        Database db,
        string? preferredStyleName,
        string? preferredGroupPrefix,
        out string resolvedStyleName)
    {
        resolvedStyleName = "";

        if (TryFindStyleName(tr, db, preferredStyleName, out var matchedStyleName))
        {
            resolvedStyleName = matchedStyleName;
            return true;
        }

        var prefix = preferredGroupPrefix?.Trim() ?? "";
        if (prefix.Length == 0)
            return false;

        var groups = ListGroups(tr, db);
        foreach (var group in groups)
        {
            if (!string.Equals(group.Prefix, prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (group.StyleNames.Count == 0)
                continue;

            resolvedStyleName = group.StyleNames[0];
            return true;
        }

        return false;
    }

    internal static bool TryGetDimStyleObjectId(Database db, string styleName, out ObjectId styleId)
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
                    _ = TryGetDimStyleObjectId(tr, database, styleName, out var resolvedStyleId);
                    return resolvedStyleId;
                });

            return !styleId.IsNull;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析标注样式 ObjectId（CAD）", ex);
            return false;
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析标注样式 ObjectId", ex);
            return false;
        }
    }

    internal static bool TryGetDimStyleObjectId(Transaction tr, Database db, string styleName, out ObjectId styleId)
    {
        styleId = ObjectId.Null;
        if (!TryFindStyleName(tr, db, styleName, out var matchedStyleName))
            return false;

        var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in dst)
        {
            var r = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (string.Equals(r.Name, matchedStyleName, StringComparison.OrdinalIgnoreCase))
            {
                styleId = id;
                return true;
            }
        }

        return false;
    }

    internal static bool TryReadSample(
        Transaction tr,
        Database db,
        string styleName,
        out DimStyleBatchFormState state,
        out string? error)
    {
        state = new DimStyleBatchFormState();
        error = null;
        var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        if (!dst.Has(styleName))
        {
            error = "未找到标注样式：" + styleName;
            return false;
        }

        var rid = dst[styleName];
        var r = (DimStyleTableRecord)tr.GetObject(rid, OpenMode.ForRead);
        state.ArrowBlockName = TryBlockName(tr, r.Dimblk);
        state.Dimasz = r.Dimasz.ToString(CultureInfo.InvariantCulture);
        state.Dimclrd = ColorToUi(r.Dimclrd);
        state.Dimexe = r.Dimexe.ToString(CultureInfo.InvariantCulture);
        state.DimfxlenOn = r.DimfxlenOn;
        state.Dimfxlen = r.Dimfxlen.ToString(CultureInfo.InvariantCulture);
        state.TextStyleName = TryTextStyleName(tr, r.Dimtxsty);
        state.Dimclrt = ColorToUi(r.Dimclrt);
        state.Dimtxt = r.Dimtxt.ToString(CultureInfo.InvariantCulture);
        state.Dimrnd = r.Dimrnd.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static string TryBlockName(Transaction tr, ObjectId bid)
    {
        if (bid.IsNull)
            return "";
        try
        {
            if (tr.GetObject(bid, OpenMode.ForRead) is BlockTableRecord btr)
                return btr.Name ?? "";
        }
        catch
        {
            // 忽略
        }

        return "";
    }

    private static string TryTextStyleName(Transaction tr, ObjectId tid)
    {
        if (tid.IsNull)
            return "";
        try
        {
            if (tr.GetObject(tid, OpenMode.ForRead) is TextStyleTableRecord tsr)
                return tsr.Name ?? "";
        }
        catch
        {
            // 忽略
        }

        return "";
    }

    private static bool TryFindStyleName(Transaction tr, Database db, string? styleName, out string matchedStyleName)
    {
        matchedStyleName = "";
        var target = styleName?.Trim() ?? "";
        if (target.Length == 0)
            return false;

        var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in dst)
        {
            var r = (DimStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!string.Equals(r.Name, target, StringComparison.OrdinalIgnoreCase))
                continue;

            matchedStyleName = r.Name ?? target;
            return true;
        }

        return false;
    }

    private static string ColorToUi(Color c)
    {
        try
        {
            if (c.ColorMethod == ColorMethod.ByLayer)
                return "BYLAYER";
            if (c.ColorMethod == ColorMethod.ByBlock)
                return "BYBLOCK";
            if (c.ColorMethod == ColorMethod.ByAci || c.ColorMethod == ColorMethod.ByColor)
                return c.ColorIndex.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            // 忽略
        }

        return "7";
    }

    internal static bool TryApply(
        Transaction tr,
        Database db,
        IReadOnlyList<string> styleNames,
        DimStyleBatchFormState values,
        out string? error)
    {
        error = null;
        if (styleNames.Count == 0)
        {
            error = "未选择标注样式。";
            return false;
        }

        if (!TryParseColor(values.Dimclrd, out var clrd))
        {
            error = "尺寸线颜色无效（可用 BYLAYER 或 0～256 索引）。";
            return false;
        }

        if (!TryParseColor(values.Dimclrt, out var clrt))
        {
            error = "文字颜色无效（可用 BYLAYER 或 0～256 索引）。";
            return false;
        }

        if (!TryParseDouble(values.Dimasz, out var dimasz) || dimasz < 0)
        {
            error = "箭头大小无效。";
            return false;
        }

        if (!TryParseDouble(values.Dimexe, out var dimexe) || dimexe < 0)
        {
            error = "尺寸界线超出尺寸线无效。";
            return false;
        }

        if (!TryParseDouble(values.Dimfxlen, out var dimfxlen) || dimfxlen < 0)
        {
            error = "固定长度值无效。";
            return false;
        }

        if (!TryParseDouble(values.Dimtxt, out var dimtxt) || dimtxt < 0)
        {
            error = "文字高度无效。";
            return false;
        }

        if (!TryParseDouble(values.Dimrnd, out var dimrnd) || dimrnd < 0)
        {
            error = "舍入值无效。";
            return false;
        }

        ObjectId arrowId = ObjectId.Null;
        if (!string.IsNullOrWhiteSpace(values.ArrowBlockName))
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(values.ArrowBlockName.Trim()))
            {
                error = "当前图纸中不存在箭头块：" + values.ArrowBlockName.Trim();
                return false;
            }

            arrowId = bt[values.ArrowBlockName.Trim()];
        }

        if (string.IsNullOrWhiteSpace(values.TextStyleName))
        {
            error = "文字样式名称不能为空。";
            return false;
        }

        var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        var tn = values.TextStyleName.Trim();
        if (!tst.Has(tn))
        {
            error = "当前图纸中不存在文字样式：" + tn;
            return false;
        }

        var textStyleId = tst[tn];

        var dst = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
        var currentStyleId = db.Dimstyle;
        DimStyleTableRecord? currentStyleRecord = null;
        foreach (var sn in styleNames)
        {
            if (!dst.Has(sn))
            {
                error = "未找到标注样式：" + sn;
                return false;
            }

            var rid = dst[sn];
            var r = (DimStyleTableRecord)tr.GetObject(rid, OpenMode.ForWrite);
            r.Dimsah = false;
            r.Dimblk = arrowId;
            r.Dimblk1 = arrowId;
            r.Dimblk2 = arrowId;
            r.Dimasz = dimasz;
            r.Dimclrd = clrd;
            r.Dimexe = dimexe;
            // 先写长度再打开开关：部分版本在 DIMFXLENON=0 时忽略对 Dimfxlen 的写入顺序
            r.Dimfxlen = dimfxlen;
            r.DimfxlenOn = values.DimfxlenOn;
            r.Dimtxsty = textStyleId;
            r.Dimclrt = clrt;
            r.Dimtxt = dimtxt;
            r.Dimrnd = dimrnd;

            if (rid == currentStyleId)
                currentStyleRecord = r;
        }

        // 仅改表记录时，当前 CAD 会话里的尺寸变量仍可能保留旧值；
        // 当前样式命中本次编辑时，显式回灌到数据库，确保立即生效。
        if (currentStyleRecord != null)
            db.SetDimstyleData(currentStyleRecord);

        return true;
    }

    private static bool TryParseDouble(string? s, out double v)
    {
        s = s?.Trim() ?? "";
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
               || double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v);
    }

    private static bool TryParseColor(string? s, out Color color)
    {
        s = s?.Trim() ?? "";
        if (s.Equals("BYLAYER", StringComparison.OrdinalIgnoreCase))
        {
            color = Color.FromColorIndex(ColorMethod.ByLayer, 256);
            return true;
        }

        if (s.Equals("BYBLOCK", StringComparison.OrdinalIgnoreCase))
        {
            color = Color.FromColorIndex(ColorMethod.ByBlock, 0);
            return true;
        }

        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            || int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out n))
        {
            if (n >= 0 && n <= 256)
            {
                color = Color.FromColorIndex(ColorMethod.ByAci, (short)n);
                return true;
            }
        }

        color = Color.FromColorIndex(ColorMethod.ByAci, 7);
        return false;
    }
}

/// <summary>批量编辑表单状态（与图纸样本同步）。</summary>
internal sealed class DimStyleBatchFormState
{
    public string ArrowBlockName { get; set; } = "";
    public string Dimasz { get; set; } = "2.5";
    public string Dimclrd { get; set; } = "BYLAYER";
    public string Dimexe { get; set; } = "0";
    public bool DimfxlenOn { get; set; }
    public string Dimfxlen { get; set; } = "1";
    public string TextStyleName { get; set; } = "";
    public string Dimclrt { get; set; } = "BYLAYER";
    public string Dimtxt { get; set; } = "2.5";
    public string Dimrnd { get; set; } = "0";
}
