namespace C_toolsPlugin;

/// <summary>本插件注册的全局命令名（见 .cursor/rules 与 PackageContents.xml）。</summary>
internal static class PluginCommandIds
{
    /// <summary>与 PackageContents.xml 中 &lt;Commands GroupName="…"&gt; 一致，便于 ApplicationPlugins 识别。</summary>
    internal const string CommandGroup = C_toolsCommandIds.CommandGroup;

    internal const string Launcher = C_toolsCommandIds.Launcher.Main;

    /// <summary>
    /// 唯一合法的图层交互命令名（提示输入图层别名，读最新 JSON；可先选对象）。勿注册或使用其它拼写变体。
    /// </summary>
    internal const string Layer = C_toolsCommandIds.MainToolset.Layer;

    /// <summary>LISP <c>CADPLUS-HATCH-START</c> 写入 <c>USERS2-5</c> 后调用：事务切层、设 <c>HP*</c>、启动 <c>HATCH</c>。</summary>
    internal const string Hatch = C_toolsCommandIds.MainToolset.Hatch;

    /// <summary>浮层「拾取填充样式」：由面板按钮 <c>SendStringToExecute</c> 触发。</summary>
    internal const string PickHatchStyle = C_toolsCommandIds.MainToolset.PickHatchStyle;

    /// <summary>带填充样式的图层快捷键：切层并启动 HATCH（独立命令，避免命令嵌套）。</summary>
    internal const string HatchLayer = C_toolsCommandIds.MainToolset.HatchLayer;

    /// <summary>HATCH 完成后修复填充图层：将最后创建的填充对象改到指定图层。</summary>
    internal const string HatchFixLayer = C_toolsCommandIds.MainToolset.HatchFixLayer;

    /// <summary>锁定布局视口；支持预选视口，若当前在视口内则直接锁定当前视口。</summary>
    internal const string ViewportLock = C_toolsCommandIds.MainToolset.ViewportLock;

    /// <summary>解锁布局视口；支持预选视口，若当前在视口内则直接解锁当前视口。</summary>
    internal const string ViewportUnlock = C_toolsCommandIds.MainToolset.ViewportUnlock;

    /// <summary>显示当前布局视口比例；若当前在视口外，则支持预选或选择单个视口。</summary>
    internal const string ViewportScaleReport = C_toolsCommandIds.MainToolset.ViewportScaleReport;

    /// <summary>按模型框选范围切回布局并创建新视口；支持从布局直接跳到模型继续取范围。</summary>
    internal const string ViewportWindow = C_toolsCommandIds.MainToolset.ViewportWindow;

    /// <summary>按所选对象所在图层保留显示并关闭其他图层；再次执行时恢复。</summary>
    internal const string LayerDisplayToggle = C_toolsCommandIds.MainToolset.LayerDisplayToggle;

    /// <summary>LAYFRZ 兜底命令；由同名入口或 LISP 桥接调用。</summary>
    internal const string LayerFreezeFallback = C_toolsCommandIds.MainToolset.LayerFreezeFallback;

    /// <summary>按设置切换所选线对象的线型、线型比例、颜色和图层；支持预选，再次执行可恢复到上一次切换前的状态。</summary>
    internal const string DashedLine = C_toolsCommandIds.MainToolset.DashedLine;

    /// <summary>弹出浮动面板，选择并切换当前图纸的标注比例。</summary>
    internal const string DimScalePick = C_toolsCommandIds.MainToolset.DimScalePick;

    /// <summary>选择单个封闭图形，读取面积并按平方米输出。</summary>
    internal const string ClosedAreaReport = C_toolsCommandIds.MainToolset.ClosedAreaReport;

    /// <summary>选择文字中的数字片段，默认按 +1 连续复制递增；放点过程中可按 S 修改增量。</summary>
    internal const string TextIncrementCopy = C_toolsCommandIds.MainToolset.TextIncrementCopy;

    /// <summary>将所选对象整体居中到所在矩形框中心；可直接选对象与框，也可只选对象后自动识别矩形框。</summary>
    internal const string RectangleCenterAlign = C_toolsCommandIds.MainToolset.RectangleCenterAlign;

    /// <summary>识别矩形范围并生成折空符号。</summary>
    internal const string FoldBreakSymbol = C_toolsCommandIds.MainToolset.FoldBreakSymbol;

    /// <summary>从布局视口内部切回视口外（纸空间）。</summary>
    internal const string SwitchToPaperSpace = C_toolsCommandIds.MainToolset.SwitchToPaperSpace;

    /// <summary>原地镜像：按所选对象中心执行左右或上下镜像，保持文字方向。</summary>
    internal const string ReflectFlip = C_toolsCommandIds.MainToolset.ReflectFlip;

    /// <summary>按所选对象中心旋转 180 度。</summary>
    internal const string Rotate180 = C_toolsCommandIds.MainToolset.Rotate180;

    /// <summary>快速启动原生 XL 的水平构造线模式，随后等待用户指定穿过点。</summary>
    internal const string XlineHorizontal = C_toolsCommandIds.MainToolset.XlineHorizontal;

    /// <summary>快速启动原生 XL 的垂直构造线模式，随后等待用户指定穿过点。</summary>
    internal const string XlineVertical = C_toolsCommandIds.MainToolset.XlineVertical;

    /// <summary>调用原生 DRAWORDER，将所选对象置于绘图次序底层。</summary>
    internal const string DrawOrderBack = C_toolsCommandIds.MainToolset.DrawOrderBack;

    /// <summary>调用原生 DRAWORDER，将所选对象置于绘图次序顶层。</summary>
    internal const string DrawOrderFront = C_toolsCommandIds.MainToolset.DrawOrderFront;

    /// <summary>根据所选填充图案调用原生命令生成边界。</summary>
    internal const string HatchBoundary = C_toolsCommandIds.MainToolset.HatchBoundary;

    /// <summary>历史别名：F_HB。</summary>
    internal const string DrawOrderFrontAlt = C_toolsCommandIds.MainToolset.DrawOrderFrontAlt;

    /// <summary>快速箭头：第一点为箭尾、第二点为箭头；取点时按 S 可设置箭尾显示文字。</summary>
    internal const string QuickArrow = C_toolsCommandIds.MainToolset.QuickArrow;

    /// <summary>快速墙体：多点取墙体基线，移动鼠标预览墙体朝向，按 S 可设置线层、填充层和双侧宽度。</summary>
    internal const string QuickWall = C_toolsCommandIds.MainToolset.QuickWall;

    /// <summary>墙面完成面：选择一根或多根直线/开放多段线，断开的对象分别生成闭合图形与填充；填充样式优先取图层命令里拾取的填充，按 S 可设置偏移量、完成面图层和完成面颜色。</summary>
    internal const string WallFinish = C_toolsCommandIds.MainToolset.WallFinish;

    /// <summary>墙面完成面遗留别名：兼容旧口径，内部仍走 F_WCC 逻辑。</summary>
    internal const string WallFinishLegacyAlias = C_toolsCommandIds.MainToolset.WallFinishLegacyAlias;

    /// <summary>浮层「图层命令」表：命令行代号与图层快捷键列一致（由 c_tools_layer_shortcuts.lsp 提供）。</summary>
    internal const string LayerShortcutCatalogCommandLabel = C_toolsCommandIds.MainToolset.LayerShortcutCatalogCommandLabel;
}
