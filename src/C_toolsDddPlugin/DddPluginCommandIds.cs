namespace C_toolsDddPlugin;

internal static class DddPluginCommandIds
{
    internal const string CommandGroup = C_toolsCommandIds.CommandGroup;

    /// <summary>文字标注浮窗：文字列表、道具列表、材料列表。</summary>
    internal const string Ddd = C_toolsCommandIds.Ddd.Main;

    /// <summary>由文字标注面板触发：按提示点取位置后插入多重引线（文本来自面板）。</summary>
    internal const string DddLeader = C_toolsCommandIds.Ddd.Leader;

    /// <summary>由面板列表触发：使用 PendingText 或上次成功插入的文字创建多重引线。</summary>
    internal const string DddInsertLeader = C_toolsCommandIds.Ddd.InsertLeader;

    /// <summary>由面板列表触发：使用 PendingText 或上次成功插入的文字创建纯文字。</summary>
    internal const string DddInsertText = C_toolsCommandIds.Ddd.InsertText;

    /// <summary>DD 插件：DIMALIGNED 交互式对齐标注，支持点内打断、点外连续和尺寸文字避让。</summary>
    internal const string DimAligned = C_toolsCommandIds.Ddd.DimAligned;

    /// <summary><see cref="DimAligned"/> 在首段原生命令完成后的内部续接命令。</summary>
    internal const string DimAlignedContinueInternal = C_toolsCommandIds.Ddd.DimAlignedContinueInternal;

    /// <summary>DD 插件：强化版线性标注（DIMLINEAR），支持连续标注、点内打断和同排文字避让。</summary>
    internal const string DimLinear = C_toolsCommandIds.Ddd.DimLinear;

    /// <summary><see cref="DimLinear"/> 在首段原生命令完成后的内部续接命令。</summary>
    internal const string DimLinearContinueInternal = C_toolsCommandIds.Ddd.DimLinearContinueInternal;

    /// <summary>DD 插件：选取单个线性/对齐标注后，自动整理同排标注文字避让。</summary>
    internal const string DimTextAvoid = C_toolsCommandIds.Ddd.DimTextAvoid;

    /// <summary>DD 插件：选取同排中的两条线性/对齐标注后，将两者之间合并为一个总尺寸。</summary>
    internal const string DimMerge = C_toolsCommandIds.Ddd.DimMerge;

    /// <summary>DD 插件：单选线性/对齐标注时按同排生成外包总尺寸；预选多条时按所选连续标注求和生成外包总尺寸。</summary>
    internal const string DimOuter = C_toolsCommandIds.Ddd.DimOuter;

    /// <summary>DD 插件：点取连续标注共用的中间标注脚，再点新位置，快速重分两侧尺寸。</summary>
    internal const string DimFootEdit = C_toolsCommandIds.Ddd.DimFootEdit;

    /// <summary>DD 插件：将 MTEXTED / TEXTED 恢复为 AutoCAD 内置文字编辑器。</summary>
    internal const string TextEditorFix = C_toolsCommandIds.Ddd.TextEditorFix;

    /// <summary>DD 插件：打开窄窗，记录单行/多行文字历史，并将历史文字快速写回当前选中文字。</summary>
    internal const string TextHistoryEdit = C_toolsCommandIds.Ddd.TextHistoryEdit;

    /// <summary>DD 插件：启动 AutoCAD 原生 TXT2MTXT，把单行文字转换为多行文字。</summary>
    internal const string TextToMText = C_toolsCommandIds.Ddd.TextToMText;
    /// <summary>DD 插件：文字转多行文字兼容命令（常见误输）。</summary>
    internal const string TextToMTextCompat = C_toolsCommandIds.Ddd.TextToMTextCompat;

    /// <summary>DD 插件：选择单行/多行文字后，按当前 DDD 引线格式插入多重引线。</summary>
    internal const string TextToLeader = C_toolsCommandIds.Ddd.TextToLeader;

    /// <summary>DD 插件：选择来源文字后，将目标文字内容匹配为来源内容。</summary>
    internal const string TextMatch = C_toolsCommandIds.Ddd.TextMatch;

    /// <summary>DD 插件：将单行/多行文字的对齐方式快速改为中间。</summary>
    internal const string TextMiddleAlign = C_toolsCommandIds.Ddd.TextMiddleAlign;

    /// <summary>DD 插件：选取线性/对齐标注、文字、引线或多重引线后，拖拽预览并快速调整位置。</summary>
    internal const string DimShift = C_toolsCommandIds.Ddd.DimShift;

    /// <summary>DD 插件：选取线性/对齐标注、文字、引线或多重引线后，拖拽预览并沿左右方向快速调整位置。</summary>
    internal const string DimShiftHorizontal = C_toolsCommandIds.Ddd.DimShiftHorizontal;
}
