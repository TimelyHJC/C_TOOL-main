using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;
using AcadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

namespace C_toolsPlugin;

/// <summary>「路径与资源」标签页：C_TOOL 数据文件与 CAD 常用路径说明。</summary>
internal static class PathsInfoCatalog
{
    internal sealed class Section
    {
        public string Title { get; init; } = "";
        public List<Row> Rows { get; } = new();
    }

    internal sealed class Row
    {
        public string Title { get; init; } = "";
        public string PathText { get; init; } = "";
        public string? Hint { get; init; }

        public Row(string title, string pathText, string? hint = null)
        {
            Title = title;
            PathText = pathText;
            Hint = hint;
        }
    }

    internal static List<Section> BuildPluginSections()
    {
        C_toolsPaths.EnsureFolders();
        var sections = new List<Section>();
        
        var s = new Section { Title = "C_TOOL 插件" };
        s.Rows.Add(new Row("应用数据根目录（Support、注册表 UserDataRoot 等）", C_toolsPaths.AppDataRoot));
        s.Rows.Add(new Row("用户可编辑目录（Configuration、.arg、初始化快捷键等）", C_toolsPaths.UserEditableFolder));
        s.Rows.Add(new Row("插件自动数据目录（JSON、PGP、LISP、最近选择记录等）", C_toolsPaths.UserConfigFolder));
        s.Rows.Add(new Row("Support 子目录（支持路径首位目录之一）", C_toolsPaths.SupportFolder));
        s.Rows.Add(new Row("合并后的命令别名 acad.pgp（CAD 实际加载）", C_toolsPaths.UserAcadPgpPath, "由 CadPgpMerge 合并原生与 C_TOOL 块"));
        s.Rows.Add(new Row("C_TOOL 别名块副本（仅备份，CAD 不直接加载）", C_toolsPaths.UserSiblingC_toolsAliasesPgpPath));
        s.Rows.Add(new Row("V_YYY 上次应用的 .arg 记录", Path.Combine(C_toolsPaths.UserConfigFolder, "V_YYY_last_arg_profile.json")));
        s.Rows.Add(new Row("V_YYY 标注样式分组上次选择", Path.Combine(C_toolsPaths.UserConfigFolder, "V_YYY_dimstyle_last_group.json")));
        s.Rows.Add(new Row("图层快捷键 JSON（C_TOOL 主面板等）",
            Path.Combine(C_toolsPaths.LayerShortcutsDataFolder, "layer_shortcuts.json")));
        s.Rows.Add(new Row("图层别名 AutoLISP",
            Path.Combine(C_toolsPaths.LayerShortcutsDataFolder, "c_tools_layer_shortcuts.lsp")));
        s.Rows.Add(new Row("命令说明缓存", Path.Combine(C_toolsPaths.UserConfigFolder, "command_descriptions.json")));
        s.Rows.Add(new Row("命令表快照", Path.Combine(C_toolsPaths.UserConfigFolder, "command_catalog_snapshot.json")));
        sections.Add(s);

        return sections;
    }

    /// <summary>须在 <see cref="DocumentManager.ExecuteInApplicationContext"/> 内调用。</summary>
    internal static List<Section> BuildCadSections()
    {
        var list = new List<Section>();
        var cad = new Section { Title = "AutoCAD 当前会话" };

        try
        {
            var acadStr = CadSystemVariableService.GetTrimmedStringOrDefault(SystemVariableNames.Acad);
            if (!string.IsNullOrWhiteSpace(acadStr))
            {
                var pretty = acadStr.Replace(";", Environment.NewLine + "  ");
                cad.Rows.Add(new Row("支持文件搜索路径（系统变量 ACAD）", pretty, "含 .lsp、.pat 等；分号分隔"));
            }
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("PathsInfoCatalog 读取 ACAD 支持路径失败", ex);
        }

        TryAddPreferencePath(cad.Rows, "打印机配置目录（PC3）", f => (string?)f.PrinterConfigPath);
        TryAddPlotStylePath(cad.Rows);

        try
        {
            var db = AcAp.DocumentManager.MdiActiveDocument?.Database;
            var pat = HostApplicationServices.Current.FindFile("acad.pat", db, FindFileHint.Default);
            if (!string.IsNullOrWhiteSpace(pat))
                cad.Rows.Add(new Row("默认填充图案 acad.pat（解析路径）", pat!, "随支持路径与安装目录解析"));
        }
        catch (AcadRuntimeException ex)
        {
            C_toolsDiagnostics.LogNonFatal("PathsInfoCatalog 解析 acad.pat 失败（CAD）", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("PathsInfoCatalog 解析 acad.pat 失败（IO）", ex);
        }

        if (cad.Rows.Count > 0)
            list.Add(cad);
        return list;
    }

    private delegate string? PrefPathGetter(dynamic files);

    private static void TryAddPreferencePath(List<Row> rows, string title, PrefPathGetter getter)
    {
        try
        {
            dynamic files = ((dynamic)AcAp.AcadApplication).Preferences.Files;
            var path = getter(files);
            if (!string.IsNullOrWhiteSpace(path))
                rows.Add(new Row(title, path!));
        }
        catch (System.Exception ex) when (ex is COMException or InvalidOperationException
                                       || string.Equals(ex.GetType().Name, "RuntimeBinderException", StringComparison.Ordinal))
        {
            C_toolsDiagnostics.LogNonFatal($"PathsInfoCatalog 读取“{title}”失败", ex);
        }
    }

    private static void TryAddPlotStylePath(List<Row> rows)
    {
        try
        {
            dynamic files = ((dynamic)AcAp.AcadApplication).Preferences.Files;
            string? path = files.PrinterStyleSheetPath;
            if (string.IsNullOrWhiteSpace(path))
                path = files.PrintStylePath;
            if (!string.IsNullOrWhiteSpace(path))
                rows.Add(new Row("打印样式表目录（CTB/STB）", path!));
        }
        catch (System.Exception ex) when (ex is COMException or InvalidOperationException
                                       || string.Equals(ex.GetType().Name, "RuntimeBinderException", StringComparison.Ordinal))
        {
            C_toolsDiagnostics.LogNonFatal("PathsInfoCatalog 读取打印样式表目录失败", ex);
        }
    }
}
