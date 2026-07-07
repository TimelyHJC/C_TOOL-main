using System;
using System.Collections.Generic;

namespace C_toolsPlugin;

/// <summary>
/// 将变量名划分到标签页。「路径与资源」标签仅展示路径说明，原归入该页的系统变量在「标注样式」标签中编辑。
/// </summary>
internal static class SysConfigTabClassifier
{
    /// <summary>与界面标签顺序一致：标注样式、路径与资源。</summary>
    internal static readonly string[] TabKeysInOrder =
    {
        "DimStyle",
        "Paths"
    };

    private static readonly Dictionary<string, string> VarToTab =
        new(StringComparer.OrdinalIgnoreCase);

    static SysConfigTabClassifier()
    {
        void AddRange(string tab, params string[] names)
        {
            foreach (var n in names)
                VarToTab[n] = tab;
        }

        // 标注样式
        AddRange("DimStyle",
            "LUNITS", "INSUNITS", "MIRRTEXT", "MEASUREINIT", "MEASUREMENT", "LWDISPLAY", "AUNITS", "ATTMODE",
            "ANNOMONITOR", "LUPREC", "ANNOALLVISIBLE", "SAVEFIDELITY", "LTSCALE", "MSLTSCALE", "CELTSCALE",
            "ANGBASE", "ANGDIR", "AUPREC", "UNITMODE", "FIELDEVAL", "CECOLOR", "CELWEIGHT", "CELTYPE");

        // 原「界面外观」：绘图窗口颜色（DSP_*）
        AddRange("DimStyle",
            "DSP_MODEL2D_BG", "DSP_UNIFIED_BG", "DSP_LAYOUT_BG", "DSP_BLOCKEDIT_BG");

        // 原「其它」：光标、选择、动态输入等（在「标注样式」标签中展示）
        AddRange("DimStyle",
            "UCSICON", "SHORTCUTMENU", "CLASSICKEYS", "TRAYICONS", "BLIPMODE", "TEXTFILL", "FILLMODE", "UCSFOLLOW",
            "DRAWORDERCTL", "LOCKUI", "MENUBAR", "QPMODE", "ROLLOVERTIPS", "TRANSPARENCYDISPLAY", "NAVBARDISPLAY",
            "INPUTSEARCHOPTIONFLAGS", "TASKBAR", "NAVVCUBEDISPLAY", "AppStatusBarUseIcons", "SHORTCUTMENUDURATION",
            "COMMANDPREVIEW", "INPUTSEARCHDELAY", "STARTMODE", "CMDECHO", "EXPERT", "QTEXTMODE",
            "APERTURE", "MBUTTONPAN", "DYNMODE", "DYNPROMPT", "COPYMODE", "APBOX", "PELLIPSE", "PLINEWID",
            "TRIMMODE", "GRIDMODE", "TRACKPATH", "HPISLANDDETECTION", "EDGEMODE", "OFFSETGAPTYPE", "UCSDETECT",
            "HPQUICKPREVIEW", "HPORIGINMODE", "MIRRHATCH", "SNAPMODE", "DRAGMODE", "OSNAPHATCH", "HPDRAWORDER", "HPGAPTOL",
            "HPINHERIT", "TEMPOVERRIDES", "ELEVATION", "THICKNESS", "ORTHOMODE", "POLARANG", "SNAPANG", "LIMCHECK",
            "PICKBOX", "HPASSOC", "PICKSTYLE", "SELECTIONPREVIEW", "HIGHLIGHT", "PICKADD", "PICKFIRST", "PICKAUTO",
            "PREVIEWEFFECT", "SELECTIONAREA", "GRIPOBJLIMIT", "SELECTIONCYCLING", "SELECTIONEFFECT", "DBLCLKEDIT",
            "CURSORSIZE", "ZOOMFACTOR", "QAFLAGS", "REGENMODE", "VTENABLE", "VTFPS", "TREEMAX", "WHIPTHREAD",
            "DEFAULTGIZMO", "HPMAXLINES", "ISOLINES", "FACETRES");

        // 原「路径与资源」表中的布局/文件类变量（在「标注样式」标签中展示）
        AddRange("DimStyle",
            "MAXACTVP", "LAYOUTREGENCTL", "THUMBSAVE", "PSLTSCALE", "UPDATETHUMBNAIL",
            "FILEDIA", "STARTUP", "XEDIT", "ACADLSPASDOC", "VISRETAIN", "REMEMBERFOLDERS", "XCLIPFRAME",
            "PROXYNOTICE", "PROXYSHOW", "ISAVEPERCENT");
    }

    internal static string GetTabKey(string varName)
    {
        if (VarToTab.TryGetValue(varName, out var tab))
            return tab;
        return "DimStyle";
    }
}
