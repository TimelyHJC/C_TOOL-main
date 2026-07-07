using System.IO;
using Autodesk.AutoCAD.DatabaseServices;

namespace C_toolsPlugin;

/// <summary>
/// 新建图层时套用说明、线型、线宽；不写入图层颜色（颜色由快捷切层时对选中图元设 <see cref="Entity.Color"/> 的 ACI）。
/// </summary>
internal static class LayerStyleHelper
{
    internal static void ApplyToNewLayer(Transaction tr, Database db, LayerTableRecord ltr, LayerShortcutEntry e)
    {
        ApplyDescription(ltr, e);

        var wantLt = string.IsNullOrWhiteSpace(e.LinetypeName) ? LinetypeNames.Continuous : e.LinetypeName!.Trim();
        ltr.LinetypeObjectId = ResolveLinetypeIdOrContinuous(tr, db, wantLt);

        ltr.LineWeight = ParseLineWeight(e.LineWeight);
    }

    internal static void ApplyDescription(LayerTableRecord ltr, LayerShortcutEntry e)
    {
        var description = NormalizeDescription(e.Description);
        if (string.Equals((ltr.Description ?? "").Trim(), description, StringComparison.Ordinal))
            return;

        ltr.Description = description;
    }

    internal static string NormalizeDescription(string? description) => (description ?? "").Trim();

    internal static bool TryResolveLinetypeId(Transaction tr, Database db, string? name, out ObjectId id)
    {
        var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        var wantedName = string.IsNullOrWhiteSpace(name) ? LinetypeNames.Continuous : name!.Trim();
        EnsureLinetypeLoaded(tr, db, ltt, wantedName);

        ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        return TryGetLinetypeId(tr, ltt, wantedName, out id);
    }

    private static ObjectId ResolveLinetypeIdOrContinuous(Transaction tr, Database db, string name)
    {
        var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        EnsureLinetypeLoaded(tr, db, ltt, name);

        ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        if (TryGetLinetypeId(tr, ltt, name, out var id))
            return id;

        if (TryGetLinetypeId(tr, ltt, LinetypeNames.Continuous, out id))
            return id;
        if (TryGetLinetypeId(tr, ltt, LinetypeNames.Continuous.ToUpperInvariant(), out id))
            return id;

        foreach (ObjectId oid in ltt)
        {
            var rec = (LinetypeTableRecord)tr.GetObject(oid, OpenMode.ForRead);
            if (string.Equals(rec.Name, LinetypeNames.Continuous, StringComparison.OrdinalIgnoreCase))
                return oid;
        }

        return ltt.Has(LinetypeNames.Continuous) ? ltt[LinetypeNames.Continuous] : ObjectId.Null;
    }

    private static void EnsureLinetypeLoaded(Transaction tr, Database db, LinetypeTable ltt, string name)
    {
        if (TryGetLinetypeId(tr, ltt, name, out _))
            return;

        if (TryLoadLineTypeFile(db, name))
            return;

        TryLoadAllLineTypes(db);
    }

    private static bool TryLoadLineTypeFile(Database db, string name)
    {
        try
        {
            db.LoadLineTypeFile(name, CadResourceFileNames.AcadLin);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"加载线型 {name} 失败（无效操作）", ex);
            return false;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"加载线型 {name} 失败（参数错误）", ex);
            return false;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"加载线型 {name} 失败（IO）", ex);
            return false;
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal($"加载线型 {name} 失败（CAD）", ex);
            return false;
        }
    }

    private static void TryLoadAllLineTypes(Database db)
    {
        try
        {
            db.LoadLineTypeFile("*", CadResourceFileNames.AcadLin);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("加载所有线型失败（无效操作）", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("加载所有线型失败（参数错误）", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("加载所有线型失败（IO）", ex);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("加载所有线型失败（CAD）", ex);
        }
    }

    private static bool TryGetLinetypeId(Transaction tr, LinetypeTable ltt, string name, out ObjectId id)
    {
        id = ObjectId.Null;
        if (ltt.Has(name))
        {
            id = ltt[name];
            return true;
        }

        foreach (ObjectId oid in ltt)
        {
            var rec = (LinetypeTableRecord)tr.GetObject(oid, OpenMode.ForRead);
            if (string.Equals(rec.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                id = oid;
                return true;
            }
        }

        return false;
    }

    internal static LineWeight ParseLineWeight(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return LineWeight.ByLineWeightDefault;
        var t = raw!.Trim();
        if (t == "0" || t == "默认" || t.Equals("default", StringComparison.OrdinalIgnoreCase))
            return LineWeight.ByLineWeightDefault;
        if (Enum.TryParse<LineWeight>(t, true, out var byName))
            return byName;
        if (int.TryParse(t, out var n) && Enum.IsDefined(typeof(LineWeight), n))
            return (LineWeight)n;
        return LineWeight.ByLineWeightDefault;
    }

    internal static int? TryParseAciColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!int.TryParse(text!.Trim(), out var v))
            return null;
        if (v < 1 || v > 255)
            return null;
        return v;
    }
}
