namespace C_toolsShared;

/// <summary>
/// 跨插件统一命令号单一事实源。
/// 各插件内本地 <c>*CommandIds</c> 仅保留语义别名，避免命令目录、Ribbon 和命令入口各自维护一份字符串。
/// </summary>
/// <remarks>
/// 命令命名规范：
/// <list type="bullet">
///   <item><description>主命令：C_TOOL 或 V_XXX（如 C_TOOL、V_AAA）</description></item>
///   <item><description>快捷命令：F_XX（如 F_Layer、F_Hatch）</description></item>
///   <item><description>内部命令：带 _ 后缀或描述性名称</description></item>
/// </list>
/// </remarks>
public static class C_toolsCommandIds
{
    /// <summary>
    /// AutoCAD 命令组名称。
    /// </summary>
    public const string CommandGroup = "CTOOL";

    /// <summary>
    /// 启动器命令。
    /// </summary>
    public static class Launcher
    {
        /// <summary>C_TOOL 唯一入口命令</summary>
        public const string Main = "C_TOOL";
    }

    /// <summary>
    /// 主插件特性程序集下的命令集合（图层快捷、填充、视口等）；面板入口已统一收口到 <see cref="Launcher.Main"/>。
    /// </summary>
    public static class MainToolset
    {
        /// <summary>主插件程序集命令前缀；用于命令目录元数据与程序集归属判断，不再注册为额外用户入口。</summary>
        public const string Main = Launcher.Main;
        /// <summary>图层快捷命令</summary>
        public const string Layer = "F_Layer";
        /// <summary>填充命令</summary>
        public const string Hatch = "F_Hatch";
        /// <summary>拾取填充样式</summary>
        public const string PickHatchStyle = "F_PickHatchStyle";
        /// <summary>填充图层</summary>
        public const string HatchLayer = "F_HatchLayer";
        /// <summary>修复填充图层</summary>
        public const string HatchFixLayer = "F_HatchFixLayer";
        /// <summary>锁定视口</summary>
        public const string ViewportLock = "F_SW";
        /// <summary>解锁视口</summary>
        public const string ViewportUnlock = "F_FW";
        /// <summary>视口比例报告</summary>
        public const string ViewportScaleReport = "F_GV";
        /// <summary>按模型框选范围创建布局视口</summary>
        public const string ViewportWindow = "F_VW";
        /// <summary>图层显示切换</summary>
        public const string LayerDisplayToggle = "F_DD";
        /// <summary>图层显示切换默认别名</summary>
        public const string LayerDisplayToggleAliasShort = "D";
        /// <summary>LAYFRZ 兜底实现；供启动脚本在原生命令缺失时调用。</summary>
        public const string LayerFreezeFallback = "F_LAYFRZ";
        /// <summary>线型切换</summary>
        public const string DashedLine = "F_XG";
        /// <summary>标注比例拾取</summary>
        public const string DimScalePick = "F_DE";
        /// <summary>封闭面积报告</summary>
        public const string ClosedAreaReport = "F_CAR";
        /// <summary>文字递增复制</summary>
        public const string TextIncrementCopy = "F_AD";
        /// <summary>矩形框内居中</summary>
        public const string RectangleCenterAlign = "F_ZZ";
        /// <summary>多个矩形合并为一个闭合图形</summary>
        public const string RectangleMerge = "F_JX";
        /// <summary>折空符号</summary>
        public const string FoldBreakSymbol = "F_DK";
        /// <summary>切换到布局空间</summary>
        public const string SwitchToPaperSpace = "F_AQ";
        /// <summary>原地镜像</summary>
        public const string ReflectFlip = "F_RF";
        /// <summary>对象中心旋转 180 度</summary>
        public const string Rotate180 = "F_SSR";
        /// <summary>水平构造线</summary>
        public const string XlineHorizontal = "F_SS";
        /// <summary>垂直构造线</summary>
        public const string XlineVertical = "F_XX";
        /// <summary>对象绘图次序后置</summary>
        public const string DrawOrderBack = "F_WB";
        /// <summary>对象绘图次序前置</summary>
        public const string DrawOrderFront = "F_SB";
        /// <summary>根据填充生成边界</summary>
        public const string HatchBoundary = "F_HB";
        /// <summary>历史常量名：F_HB 现用于根据填充生成边界。</summary>
        public const string DrawOrderFrontAlt = HatchBoundary;
        /// <summary>快捷箭头</summary>
        public const string QuickArrow = "F_JT";
        /// <summary>快捷墙体</summary>
        public const string QuickWall = "F_SQT";
        /// <summary>墙面完成面</summary>
        public const string WallFinish = "F_WCC";
        /// <summary>墙面完成面遗留别名</summary>
        public const string WallFinishLegacyAlias = "F_ZY";
        /// <summary>图层快捷目录命令标签</summary>
        public const string LayerShortcutCatalogCommandLabel = "（别名即命令）";
    }

    /// <summary>
    /// V_YYY 系统配置插件命令。
    /// </summary>
    public static class Sys
    {
        /// <summary>V_YYY 主入口命令</summary>
        public const string Main = "V_YYY";
        /// <summary>短别名</summary>
        public const string AliasShort = "YY";
        /// <summary>打开标注样式选项卡</summary>
        public const string OpenDimStyleTab = "F_YYY";
        /// <summary>打开打印与保存选项卡</summary>
        public const string OpenPrintSaveTab = "F_YYY_PRINTSAVE";
    }

    /// <summary>
    /// V_AAA 图块库插件命令。
    /// </summary>
    public static class Aaa
    {
        /// <summary>V_AAA 主入口命令</summary>
        public const string Main = "V_AAA";
        /// <summary>短别名</summary>
        public const string AliasShort = "AA";
        /// <summary>QL 图块列表入口</summary>
        public const string Ql = "F_QL";
    }

    /// <summary>
    /// V_BBB 设备清单插件命令。
    /// </summary>
    public static class Bbb
    {
        /// <summary>V_BBB 主入口命令</summary>
        public const string Main = "V_BBB";
        /// <summary>短别名</summary>
        public const string AliasShort = "BB";
        /// <summary>文字转属性</summary>
        public const string TextToAttribute = "F_zsx";
        /// <summary>创建设备块</summary>
        public const string DeviceBlockCreate = "F_AB";
        /// <summary>刷新选中图块的增强属性显示</summary>
        public const string BlockAttributeRefresh = "F_BV";
        /// <summary>同步块属性参照</summary>
        public const string BlockAttributeSync = BlockAttributeRefresh;
        /// <summary>历史命令：块分配隐藏设备名（已停用）</summary>
        public const string BlockAssignHiddenDeviceNames = "F_BXR";
        /// <summary>历史命令：按映射汇总设备名并导出表格（已停用）</summary>
        public const string BlockExportMappedDeviceNames = "F_BXF";
    }

    /// <summary>
    /// V_DDD 文字标注插件命令。
    /// </summary>
    public static class Ddd
    {
        /// <summary>V_DDD 主入口命令</summary>
        public const string Main = "V_DDD";
        /// <summary>短别名</summary>
        public const string AliasShort = "DD";
        /// <summary>引线命令</summary>
        public const string Leader = "F_DddLeader";
        /// <summary>插入引线</summary>
        public const string InsertLeader = "F_DDD_INSERT_LEADER";
        /// <summary>插入文字</summary>
        public const string InsertText = "F_DDD_INSERT_TEXT";
        /// <summary>对齐标注</summary>
        public const string DimAligned = "F_DA";
        /// <summary>对齐标注连续（内部）</summary>
        public const string DimAlignedContinueInternal = "F_DA_CONTINUE_INTERNAL";
        /// <summary>线性标注</summary>
        public const string DimLinear = "F_DC";
        /// <summary>线性标注连续（内部）</summary>
        public const string DimLinearContinueInternal = "F_DC_CONTINUE_INTERNAL";
        /// <summary>标注文字避让</summary>
        public const string DimTextAvoid = "F_DF";
        /// <summary>标注合并</summary>
        public const string DimMerge = "F_DV";
        /// <summary>外包总尺寸</summary>
        public const string DimOuter = "F_DQQ";
        /// <summary>标注脚注编辑</summary>
        public const string DimFootEdit = "F_DDE";
        /// <summary>文字编辑修复</summary>
        public const string TextEditorFix = "F_TextEditFix";
        /// <summary>历史文字快改</summary>
        public const string TextHistoryEdit = "F_ED";
        /// <summary>文字转多行文字</summary>
        public const string TextToMText = "F_TTM";
        /// <summary>文字转多行文字兼容命令（常见误输）。</summary>
        public const string TextToMTextCompat = "F_TMM";
        /// <summary>选择文字后插入多重引线</summary>
        public const string TextToLeader = "F_DDC";
        /// <summary>匹配文字内容</summary>
        public const string TextMatch = "F_AT";
        /// <summary>文字对齐方式改为中间</summary>
        public const string TextMiddleAlign = "F_TM";
        /// <summary>标注拖拽调整</summary>
        public const string DimShift = "F_DS";
        /// <summary>标注水平拖拽</summary>
        public const string DimShiftHorizontal = "F_DZ";
    }

    /// <summary>
    /// V_QQQ 打印插件命令。
    /// </summary>
    public static class Qqq
    {
        /// <summary>V_QQQ 主入口命令</summary>
        public const string Main = "V_QQQ";
        /// <summary>短别名</summary>
        public const string AliasShort = "QQ";
    }
}
