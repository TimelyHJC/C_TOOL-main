namespace C_toolsPlugin;

/// <summary>插件内共享的默认与魔法字符串，避免散落重复。</summary>
internal static class C_toolsConstants
{
    internal const string DefaultHatchPattern = "ANSI31";
    internal const double DefaultHatchScale = 1.0;
    internal const int MaxHatchPatternLength = 34;
}

/// <summary>AutoCAD 常用线型名称常量。</summary>
internal static class LinetypeNames
{
    internal const string Continuous = "Continuous";
    internal const string Dashed = "DASHED";
}

/// <summary>AutoCAD 资源文件名称常量。</summary>
internal static class CadResourceFileNames
{
    internal const string AcadLin = "acad.lin";
}

/// <summary>AutoCAD 系统变量名称常量（已移至 C_toolsShared.SystemVariableNames，此处保留别名以保持向后兼容）。</summary>
internal static class SystemVariableNames
{
    internal const string Users1 = C_toolsShared.SystemVariableNames.Users1;
    internal const string Users2 = C_toolsShared.SystemVariableNames.Users2;
    internal const string Users3 = C_toolsShared.SystemVariableNames.Users3;
    internal const string Users4 = C_toolsShared.SystemVariableNames.Users4;
    internal const string Users5 = C_toolsShared.SystemVariableNames.Users5;
    internal const string TextSize = C_toolsShared.SystemVariableNames.TextSize;
    internal const string Insunits = C_toolsShared.SystemVariableNames.Insunits;
    internal const string HpName = C_toolsShared.SystemVariableNames.HpName;
    internal const string HpScale = C_toolsShared.SystemVariableNames.HpScale;
    internal const string HpAngle = C_toolsShared.SystemVariableNames.HpAngle;
    internal const string HpQuickPreview = C_toolsShared.SystemVariableNames.HpQuickPreview;
    internal const string HpOriginMode = C_toolsShared.SystemVariableNames.HpOriginMode;
    internal const string LtScale = C_toolsShared.SystemVariableNames.LtScale;
    internal const string PsLtScale = C_toolsShared.SystemVariableNames.PsLtScale;
    internal const string FileDia = C_toolsShared.SystemVariableNames.FileDia;
    internal const string CmdEcho = C_toolsShared.SystemVariableNames.CmdEcho;
    internal const string Cannoscale = C_toolsShared.SystemVariableNames.Cannoscale;
    internal const string CvPort = C_toolsShared.SystemVariableNames.CvPort;
    internal const string Clayer = C_toolsShared.SystemVariableNames.Clayer;
    internal const string MirrText = C_toolsShared.SystemVariableNames.MirrText;
    internal const string CurrentMLeaderStyle = C_toolsShared.SystemVariableNames.CurrentMLeaderStyle;
    internal const string Acad = C_toolsShared.SystemVariableNames.Acad;
    internal const string WsCurrent = C_toolsShared.SystemVariableNames.WsCurrent;
}

/// <summary>命令名称常量。</summary>
internal static class CommandNames
{
    internal const string Layer = "_.-LAYER";
    internal const string Hatch = "_.HATCH";
    internal const string DimAligned = "._DIMALIGNED";
    internal const string Rotate = "_.ROTATE";
    internal const string Mirror = "_.MIRROR";
    internal const string DrawOrder = "_.DRAWORDER";
    internal const string DrawOrderFrontOption = "_Front";
    internal const string DrawOrderBackOption = "_Back";
    internal const string HatchGenerateBoundary = "_.HATCHGENERATEBOUNDARY";
    internal const string Xline = "_.XLINE";
    internal const string XlineHorizontalOption = "_H";
    internal const string XlineVerticalOption = "_V";
}
