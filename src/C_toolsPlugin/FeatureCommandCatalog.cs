using System.IO;

namespace C_toolsPlugin;

/// <summary>
/// 集中维护跨插件的用户可见命令元数据，避免命令目录分类与默认说明分散在多个特判里。
/// </summary>
internal static class FeatureCommandCatalog
{
    private static readonly CommandEntry[] s_entries =
    {
        Visible(PluginCommandIds.FoldBreakSymbol, "识别矩形范围并生成折空符号；按 S 可设置转折比例和 ACI 颜色", C_toolsCommandIds.MainToolset.Main),
        VisibleRibbon(PluginCommandIds.Launcher, "打开面板", "打开 C_TOOL 集成面板，统一查看图层快捷键、插件命令、路径说明与命令目录"),
        Visible(PluginCommandIds.Layer, "LISP 图层别名将 USERS1 写入后调用本命令 → RunByAlias；手动执行无 USERS1 时仅提示用别名单独命令；可先选对象", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.Hatch, "从 USERS2-5 读取图层、图案、比例、角度后启动 HATCH", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.PickHatchStyle, "从图中拾取已有填充样式，并回填到当前图层快捷键行", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.HatchLayer, "从 USERS1-4 读取图层与填充参数，切层后启动 HATCH", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.HatchFixLayer, "HATCH 完成后，将最近创建的填充对象修正到目标图层", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.ViewportLock, "锁定布局视口；支持预选视口，若当前在视口内则直接锁定当前视口", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.ViewportUnlock, "解锁布局视口；支持预选视口，若当前在视口内则直接解锁当前视口", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.ViewportScaleReport, "显示当前布局视口比例；若当前在视口外，则支持预选或选择单个视口", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.ViewportWindow, "切到模型框选目标范围，输入比例后返回布局，单击放置并创建新视口", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.LayerDisplayToggle, "按所选对象所在图层保留显示并关闭其他图层；再次执行同命令恢复上次关闭的图层", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.DashedLine, "按设置切换所选线对象的线型、线型比例、颜色和图层；选择时按 S 可打开设置窗口，已切到当前设置的对象再次执行可恢复到上一次切换前的状态", C_toolsCommandIds.MainToolset.Main),
        VisibleRibbon(PluginCommandIds.DimScalePick, "标注比例", "弹出浮动面板，选择并切换当前图纸标注比例", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.ClosedAreaReport, "选择单个封闭图形，读取面积并按当前图纸单位换算为平方米输出", C_toolsCommandIds.MainToolset.Main),
        VisibleRibbon(PluginCommandIds.TextIncrementCopy, "递增复制", "选择文字中的数字片段后连续放点复制，默认增量 +1，放点时按 S 可修改增量", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.RectangleCenterAlign, "将所选对象整体居中到矩形框中心；若当前选择里包含矩形框则优先使用，否则自动识别包住对象的最小矩形框，识别不到时可手动点选", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.SwitchToPaperSpace, "从布局视口内部切回视口外（纸空间）", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.ReflectFlip, "原地镜像：先选择对象，再按选区中心执行左右或上下镜像，保持文字方向不变", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.Rotate180, "选择对象后，按所选对象整体中心旋转 180 度", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.XlineHorizontal, "快速启动原生 XL 的水平构造线模式，并继续等待用户指定穿过点", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.XlineVertical, "快速启动原生 XL 的垂直构造线模式，并继续等待用户指定穿过点", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.DrawOrderBack, "调用 CAD 原生 DRAWORDER，将所选对象后置到绘图次序底层", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.DrawOrderFront, "调用 CAD 原生 DRAWORDER，将所选对象前置到绘图次序顶层", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.DrawOrderFrontAlt, "调用 CAD 原生 DRAWORDER，将所选对象前置到绘图次序顶层", C_toolsCommandIds.MainToolset.Main),
        VisibleRibbon(PluginCommandIds.QuickArrow, "快速箭头", "快速箭头：第一点指定箭尾、第二点指定箭头；取点时按 S 可设置箭尾显示文字", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.QuickWall, "快速墙体：多点取墙体基线，移动鼠标预览墙体朝向，按 S 可设置线层、颜色和串联双层宽度，按 X 切换单层/双层，按 F 互换两项宽度", C_toolsCommandIds.MainToolset.Main),
        Visible(PluginCommandIds.WallFinish, "墙面完成面：选择一根或多根直线/开放多段线作为墙面线，断开的对象分别生成闭合图形与填充；填充样式优先取图层命令里拾取的填充，按 S 可设置偏移量、完成面图层和完成面颜色", C_toolsCommandIds.MainToolset.Main),
        Hidden(PluginCommandIds.WallFinishLegacyAlias),

        Hidden(C_toolsCommandIds.Sys.AliasShort),
        Visible(C_toolsCommandIds.Sys.Main, "打开系统配置浮窗；含标注样式、打印与保存、路径说明，并可从 .arg 安全应用系统配置"),
        VisibleOther(C_toolsCommandIds.Sys.OpenDimStyleTab, "打开系统配置浮窗并切到“标注样式”页", C_toolsCommandIds.Sys.Main),
        VisibleOther(C_toolsCommandIds.Sys.OpenPrintSaveTab, "打开系统配置浮窗并切到“打印与保存”页", C_toolsCommandIds.Sys.Main),

        Hidden(C_toolsCommandIds.Aaa.AliasShort),
        Visible(C_toolsCommandIds.Aaa.Main, "打开文件夹图块面板，可浏览目录中的 DWG，并将当前图纸中的图块导入图库"),
        Visible(C_toolsCommandIds.Aaa.Ql, "按需调用外部图块列表扩展，显示当前 DWG 图块列表", C_toolsCommandIds.Aaa.Main),

        Hidden(C_toolsCommandIds.Bbb.AliasShort),
        Visible(C_toolsCommandIds.Bbb.Main, "打开设备清单输出浮窗，可读取图块显示设备名称并写入 Excel 设备清单"),
        VisibleOther(C_toolsCommandIds.Bbb.TextToAttribute, "快速把选中的 DBText/MText 转成带“设备名称”属性的块参照", C_toolsCommandIds.Bbb.Main),
        VisibleOther(C_toolsCommandIds.Bbb.DeviceBlockCreate, "选择对象创建成设备块，弹窗以九宫格指定基点，块名可从 CAD 文字拾取", C_toolsCommandIds.Bbb.Main),

        Hidden(C_toolsCommandIds.Ddd.AliasShort),
        VisibleRibbon(C_toolsCommandIds.Ddd.Main, "打开 V_DDD", "打开文字标注浮窗，提供多重引线、智能标注与文字处理工具"),
        HiddenRibbon(C_toolsCommandIds.Ddd.Leader, "插入引线", "交互式插入多重引线；通常由文字标注面板或 Ribbon 入口触发"),
        Hidden(C_toolsCommandIds.Ddd.InsertLeader),
        Hidden(C_toolsCommandIds.Ddd.InsertText),
        VisibleRibbon(C_toolsCommandIds.Ddd.DimAligned, "对齐标注", "智能标注工具：DIMALIGNED 交互式对齐标注，支持点内打断、点外连续和尺寸文字避让", C_toolsCommandIds.Ddd.Main),
        Hidden(C_toolsCommandIds.Ddd.DimAlignedContinueInternal),
        VisibleRibbon(C_toolsCommandIds.Ddd.DimLinear, "线性标注", "智能标注工具：强化版线性标注（DIMLINEAR），支持连续标注、点内打断与同排尺寸文字避让", C_toolsCommandIds.Ddd.Main),
        Hidden(C_toolsCommandIds.Ddd.DimLinearContinueInternal),
        VisibleRibbon(C_toolsCommandIds.Ddd.DimTextAvoid, "文字避让", "智能标注工具：选中单个线性/对齐标注后，自动整理同排标注文字并分层避让", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.DimMerge, "合并尺寸", "智能标注工具：选中同排中的两条线性/对齐标注后，将两者之间的连续尺寸合并为一个总尺寸", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.DimOuter, "外包总尺", "智能标注工具：单选线性/对齐标注时按同排生成外包总尺寸；预选多条时按所选标注逐个向外偏移 360 生成外包尺寸", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.DimFootEdit, "标注脚", "智能标注工具：点击连续标注共用的中间标注脚后，再点新位置，快速把两侧尺寸重新分配", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.TextEditorFix, "修复编辑器", "辅助修复：将 MTEXTED 与 TEXTED 恢复为 AutoCAD 内置文字编辑器，处理“无法找到 SHELL 程序”", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.TextHistoryEdit, "文字快改", "文字工具：打开窄窗口，自动记录单行/多行文字历史，并将历史文字快速写回当前选中文字", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.TextToMText, "单转多", "文字工具：启动 AutoCAD 原生 TXT2MTXT，将单行文字转换为多行文字", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.TextMatch, "匹配文字", "文字工具：选择来源文字或多重引线文字后，将目标文字/多重引线文字内容匹配为来源内容；支持预选目标", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.DimShift, "上下快调", "智能标注工具：选中线性/对齐标注、文字、引线或多重引线后，拖拽预览并快速调整位置", C_toolsCommandIds.Ddd.Main),
        VisibleRibbon(C_toolsCommandIds.Ddd.DimShiftHorizontal, "左右快调", "智能标注工具：选中线性/对齐标注、文字、引线或多重引线后，拖拽预览并左右快速调整位置", C_toolsCommandIds.Ddd.Main),

        Hidden(C_toolsCommandIds.Qqq.AliasShort),
        Visible(C_toolsCommandIds.Qqq.Main, "打开批量打印浮窗；设置按钮会跳转到 V_YYY 的“打印与保存”页，并按该页保存的参数出图")
    };

    private static readonly Dictionary<string, CommandEntry> s_byCommand =
        s_entries.ToDictionary(x => x.CommandName, StringComparer.OrdinalIgnoreCase);

    internal static bool TryGetDescription(string? commandName, out string description)
    {
        description = "";
        if (!TryGetEntry(commandName, out var entry))
            return false;

        if (string.IsNullOrWhiteSpace(entry.Description))
            return false;

        description = entry.Description;
        return true;
    }

    internal static bool TryGetRibbonButtonInfo(string? commandName, out string buttonText, out string toolTip)
    {
        buttonText = "";
        toolTip = "";
        if (!TryGetEntry(commandName, out var entry))
            return false;

        if (string.IsNullOrWhiteSpace(entry.RibbonButtonText))
            return false;

        buttonText = entry.RibbonButtonText;
        toolTip = string.IsNullOrWhiteSpace(entry.Description)
            ? $"执行 {entry.CommandName}。"
            : $"{entry.CommandName}：{entry.Description}";
        return true;
    }

    internal static bool IsOwnedByCtools(string? commandName)
    {
        return TryGetEntry(commandName, out _);
    }

    internal static bool ShouldHideFromCatalog(string? commandName)
    {
        return TryGetEntry(commandName, out var entry) && !entry.VisibleInCatalog;
    }

    internal static bool ShouldShowOnVCommandTab(string? commandName, string? dotNetDllFileName)
    {
        return TryGetEntry(commandName, out var entry) &&
               entry.VisibleInCatalog &&
               entry.ShowOnVCommandTab &&
               entry.MatchesAssembly(dotNetDllFileName);
    }

    internal static bool ShouldShowOnPluginCommandTab(string? commandName, string? dotNetDllFileName)
    {
        return TryGetEntry(commandName, out var entry) &&
               entry.VisibleInCatalog &&
               !entry.ShowOnVCommandTab &&
               entry.MatchesAssembly(dotNetDllFileName);
    }

    private static bool TryGetEntry(string? commandName, out CommandEntry entry)
    {
        var normalized = (commandName ?? "").Trim();
        if (normalized.Length == 0)
        {
            entry = CommandEntry.Empty;
            return false;
        }

        return s_byCommand.TryGetValue(normalized, out entry!);
    }

    private static CommandEntry Visible(string commandName, string description, params string[] ownerAssemblyPrefixes)
    {
        return new CommandEntry(commandName, description, ribbonButtonText: "", visibleInCatalog: true, showOnVCommandTab: true, ownerAssemblyPrefixes);
    }

    private static CommandEntry VisibleRibbon(string commandName, string ribbonButtonText, string description, params string[] ownerAssemblyPrefixes)
    {
        return new CommandEntry(commandName, description, ribbonButtonText, visibleInCatalog: true, showOnVCommandTab: true, ownerAssemblyPrefixes);
    }

    private static CommandEntry VisibleOther(string commandName, string description, params string[] ownerAssemblyPrefixes)
    {
        return new CommandEntry(commandName, description, ribbonButtonText: "", visibleInCatalog: true, showOnVCommandTab: false, ownerAssemblyPrefixes);
    }

    private static CommandEntry Hidden(string commandName)
    {
        return new CommandEntry(commandName, description: "", ribbonButtonText: "", visibleInCatalog: false, showOnVCommandTab: false, Array.Empty<string>());
    }

    private static CommandEntry HiddenRibbon(string commandName, string ribbonButtonText, string description)
    {
        return new CommandEntry(commandName, description, ribbonButtonText, visibleInCatalog: false, showOnVCommandTab: false, Array.Empty<string>());
    }

    private sealed class CommandEntry
    {
        internal static readonly CommandEntry Empty = new("", "", "", visibleInCatalog: false, showOnVCommandTab: false, Array.Empty<string>());

        internal CommandEntry(
            string commandName,
            string description,
            string ribbonButtonText,
            bool visibleInCatalog,
            bool showOnVCommandTab,
            params string[] ownerAssemblyPrefixes)
        {
            CommandName = commandName;
            Description = description;
            RibbonButtonText = ribbonButtonText;
            VisibleInCatalog = visibleInCatalog;
            ShowOnVCommandTab = showOnVCommandTab;
            OwnerAssemblyPrefixes = ownerAssemblyPrefixes;
        }

        internal string CommandName { get; }
        internal string Description { get; }
        internal string RibbonButtonText { get; }
        internal bool VisibleInCatalog { get; }
        internal bool ShowOnVCommandTab { get; }
        private string[] OwnerAssemblyPrefixes { get; }

        internal bool MatchesAssembly(string? dotNetDllFileName)
        {
            if (OwnerAssemblyPrefixes.Length == 0)
                return true;

            var assemblyName = Path.GetFileNameWithoutExtension(dotNetDllFileName ?? "");
            if (string.IsNullOrWhiteSpace(assemblyName))
                return false;

            foreach (var prefix in OwnerAssemblyPrefixes)
            {
                if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
