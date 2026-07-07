using System;
using System.IO;

namespace C_toolsShared;

/// <summary>
/// UI 文字常量集中管理。
/// 便于统一维护和修改，避免硬编码字符串分散在代码各处。
/// </summary>
public static class UIMessages
{
    /// <summary>命令消息前缀</summary>
    public const string Prefix_C_TOOL = "C_TOOL：";
    public const string Prefix_V_AAA = "V_AAA：";
    public const string Prefix_V_BBB = "V_BBB：";
    public const string Prefix_V_DDD = "V_DDD：";
    public const string Prefix_V_QQQ = "V_QQQ：";
    public const string Prefix_V_YYY = "V_YYY：";
    public const string Prefix_F_zsx = "F_zsx：";
    public const string Prefix_F_BXR = "F_BXR：";

    /// <summary>通用消息</summary>
    public static class Common
    {
        public const string Ready = "就绪。";
        public const string Cancelled = "已取消。";
        public const string Success = "操作完成。";
        public const string NoSelection = "未选择对象。";
        public const string InvalidSelection = "选择无效。";
        public const string OperationFailed = "操作失败。";
        public const string PleaseSelect = "请选择对象。";
        public const string NoBlocksToProcess = "无块可处理。";

        public static string BuildSimpleFailure(string action, Exception? ex = null) =>
            BuildSimpleFailure(action, GetSimpleReason(ex));

        public static string BuildSimpleFailure(string action, string? reason)
        {
            var actionText = string.IsNullOrWhiteSpace(action) ? "操作" : action.Trim();
            return string.IsNullOrWhiteSpace(reason)
                ? $"{actionText}失败。"
                : $"{actionText}失败：{reason}。";
        }

        public static string GetSimpleReason(Exception? ex)
        {
            if (ex == null)
                return "";

            return ex switch
            {
                FileNotFoundException => "文件不存在",
                DirectoryNotFoundException => "文件夹不存在",
                UnauthorizedAccessException => "没有权限",
                InvalidDataException => "文件内容无效",
                IOException => "文件被占用",
                _ => ""
            };
        }
    }

    /// <summary>图层相关</summary>
    public static class Layer
    {
        public const string PanelTitle = "图层命令";
        public const string ShortcutHeader = "图层快捷键";
        public const string LayerNameHeader = "图层名称";
        public const string ColorHeader = "颜色(ACI)";
        public const string LinetypeHeader = "线型";
        public const string LineweightHeader = "线宽";
        public const string AddAlias = "添加图层别名";
        public const string LoadFromDrawing = "加载当前图纸图层";
        public const string ImportFromExcel = "从 Excel 导入";
        public const string Refresh = "刷新";
        public const string Save = "保存";
    }

    /// <summary>设备清单相关</summary>
    public static class Device
    {
        public const string PanelTitle = "C_TOOL 设备清单";
        public const string PanelDescription = "汇总设备名称与数量，写入 Excel 模板。";
        public const string ReadBlocks = "读取块";
        public const string SelectTemplate = "选模板";
        public const string Write = "写入";
        public const string ExcelTemplate = "Excel 模板";
        public const string DeviceTypeHeader = "设备种类";
        public const string QuantityHeader = "总数量";
        public const string DeviceNameHeader = "设备名称";
        public const string NoDeviceName = "未配置设备名称";
    }

    /// <summary>图库相关</summary>
    public static class BlockLibrary
    {
        public const string PanelTitle = "C_TOOL 图库";
        public const string PanelDescription = "浏览图块与组合；单击插入，或从图纸写入。";
        public const string SelectFolder = "选择文件夹";
        public const string Reload = "重新读取";
        public const string WriteToLibrary = "写入图库";
        public const string WriteCombo = "写入组合";
        public const string FavoriteCurrent = "收藏当前";
        public const string RemoveFavorite = "取消收藏";
        public const string FavoriteFolders = "收藏目录";
        public const string IncludeSubfolders = "包含子文件夹";
        public const string InsertSelected = "插入选中项";
        public const string BlockCount = "共 {0} 个图块/组合";
        public const string BlockCategory = "图库分类";
        public const string ThumbnailPreview = "缩略图预览";
        public const string NoSelection = "选择一个图块或组合以查看缩略图。";
        public const string NoItemSelected = "未选择项";
        public const string CancelWriteCombo = "已取消写入组合。";
        public const string CancelImportBlock = "已取消导入图块。";
        public const string CancelInsertBlock = "已取消图块插入。";
        public const string CancelInsertCombo = "已取消组合插入。";
        public const string NoActiveDocument = "当前没有活动图纸。";
        public const string InvalidFolder = "请选择有效的图块文件夹。";
        public const string NeedTwoBlocks = "至少需要选择 2 个图块才能写入组合。";
        public const string ComboNeedTwoBlocks = "组合至少需要 2 个可识别图块。";
        public const string NoBlockToInsert = "当前没有可插入的图块/组合。";
        public const string ResourceNotFound = "资源不存在：{0}";
        public const string InsertedBlock = "已插入图块：{0}";
        public const string InsertedCombo = "已插入组合：{0}";
    }

    /// <summary>打印相关</summary>
    public static class Plot
    {
        public const string PanelTitle = "C_TOOL批量打印";
        public const string Preview = "预览";
        public const string StartPrint = "开始打印";
        public const string PrintSettings = "打印参数设置";
        public const string Printer = "打印机";
        public const string Paper = "纸张";
        public const string StyleSheet = "样式表";
        public const string Scale = "比例";
        public const string FrameDetection = "图框识别方式";
        public const string BlockNameHeader = "图块名";
        public const string OutputDirectory = "输出目录";
        public const string NamingTemplate = "命名模板";
        public const string DetectionScope = "识别范围";
        public const string FrameList = "图纸列表";
        public const string FrameCount = "{0}张图纸";
    }

    /// <summary>标注相关</summary>
    public static class Dimension
    {
        public const string AlignedDimOnly = "预选标注不支持续接，请选线性或对齐标注。";
        public const string AlignedIdentified = "已识别对齐标注，请在左/右/内侧指定终点。";
        public const string InvalidDirection = "尺寸方向无效，无法投影。";
        public const string PointsCoincident = "点重合，已忽略。";
        public const string DimensionBroken = "已打断标注段。";
        public const string ContinueLeft = "请在所选标注左侧继续点取，标注会保持在同一排。";
        public const string ContinueRight = "请在所选标注右侧继续点取，标注会保持在同一排。";
        public const string AlignedAdded = "已追加对齐标注，继续点终点。";
        public const string TextAvoidComplete = "F_DF 整理 {0} 个标注，{1} 个文字调整。";
        public const string TextAvoidNoChange = "F_DF 检查 {0} 个标注，无需调整。";
    }

    /// <summary>标注拖拽相关</summary>
    public static class DimensionShift
    {
        public const string NotSupported = "F_DS 当前仅支持线性/对齐标注、文字、引线或多重引线。";
        public const string Cancelled = "F_DS 已取消。";
        public const string TextPositionUnchanged = "文字位置未变。";
        public const string TextPositionAdjusted = "F_DS 已调整当前文字位置。";
        public const string DragTextPrompt = "拖拽文字新位置，单击确认。";
        public const string DragDimensionPrompt = "已识别 {0} 个{1}，拖拽预览后单击确认新文字位置。";
        public const string DragLeaderPrompt = "已识别引线，拖拽预览后单击确认新位置。";
    }

    /// <summary>隐形设备名称相关</summary>
    public static class HiddenDeviceName
    {
        public const string PanelTitle = "F_BXR — 块隐形设备名称";
        public const string PanelDescription = "历史功能页；F_BXR 写块已停用，不再修改选中块属性。";
        public const string FieldHeader = "字段";
        public const string SelectedBlocksHeader = "选中块";
        public const string DeviceNameHeader = "设备名称";
        public const string HandleHeader = "句柄";
        public const string BlockNameHeader = "块名称";
        public const string StateHeader = "形态/类型";
        public const string CurrentHiddenNameHeader = "当前隐形设备名称";
        public const string SelectAll = "全选筛选项";
        public const string ClearSelection = "清空勾选";
        public const string SelectedBlocksSection = "选中块";
        public const string SelectedBlocksDescription = "历史说明：原用于显示待写入块列表；当前 F_BXR 已停用写块，仅保留兼容入口。";
        public const string DeviceNameListSection = "设备名称列表";
        public const string DeviceNameListDescription = "历史说明：原用于从 Excel 选择名称并写入隐藏属性；当前 F_BXR 已停用写块。";
        public const string PleaseSelectDeviceNames = "请勾选设备名称。";
    }

    /// <summary>表格列头</summary>
    public static class GridHeaders
    {
        public const string Index = "序号";
        public const string Type = "类型";
        public const string Name = "名称";
        public const string Size = "尺寸";
        public const string Select = "选择";
        public const string FrameName = "图框名";
        public const string Space = "空间";
        public const string Layout = "布局";
        public const string Layer = "图层";
        public const string Subfolder = "子目录";
        public const string ModifiedTime = "修改时间";
        public const string FileSize = "大小";
        public const string Description = "说明";
        public const string Remark = "备注";
    }

    /// <summary>命令操作消息</summary>
    public static class Command
    {
        public const string Cancelled = "已取消。";
        public const string CommandCancelled = "{0} 已取消。";
        public const string NoObjectSelected = "未选择任何对象。";
        public const string NoTextSelected = "未选择任何文字。";
        public const string NoBlockSelected = "未选择任何图块。";
        public const string NoFrameSelected = "未选择任何图框对象。";
        public const string NoDeviceBlockSelected = "未选择任何设备图块。";
        public const string NoViewportSelected = "未选择可{0}的视口。";
        public const string LayerSwitched = "当前层已切换为: {0}（未选择对象，仅切当前层）。";
        public const string CancelPickStyle = "已取消拾取样式。";
        public const string CancelSelectText = "已取消选文字。";
        public const string CancelSelectNumber = "已取消选数字。";
        public const string CancelChangeIncrement = "已取消改增量。";
        public const string CancelChangeArrowText = "已取消改箭尾文字。";
        public const string CancelFavorite = "已取消收藏：{0}";
    }

    /// <summary>标注命令消息</summary>
    public static class DimensionCommand
    {
        public const string F_DF_Cancelled = "F_DF 已取消。";
        public const string F_DV_Cancelled = "F_DV 已取消。";
        public const string F_DQQ_Cancelled = "F_DQQ 已取消。";
        public const string F_DDE_Cancelled = "F_DDE 已取消。";
        public const string F_JT_Cancelled = "F_JT 已取消。";
        public const string F_AD_Cancelled = "F_AD 已取消。";
        public const string F_DD_Cancelled = "F_DD 已取消。";
    }

    /// <summary>引线相关</summary>
    public static class Leader
    {
        public const string SetLeaderFailed = "设置引线失败：{0}";
        public const string InsertTextFailed = "插入文字失败：{0}";
        public const string NoMLeaderStyleSelected = "当前未选择多重引线样式。";
    }

    /// <summary>安装程序消息</summary>
    public static class Setup
    {
        public const string InstallInProgress = "安装仍在进行中，请等待当前操作完成后再关闭窗口。";
        public const string InstallComplete = "安装完成。";
        public const string InstallFailed = "安装失败：{0}";
        public const string UninstallComplete = "卸载完成。";
        public const string PleaseWait = "请稍候...";
    }

    /// <summary>错误消息</summary>
    public static class Errors
    {
        public const string InvalidPrinter = "未选择有效打印机。";
        public const string InvalidPath = "路径无效。";
        public const string FileNotFound = "文件未找到。";
        public const string NoPermission = "没有权限。";
        public const string ConversionFailed = "转换失败：{0}";
        public const string ExecutionFailed = "执行失败：{0}";
        public const string NoDimStyleSelected = "未选择标注样式。";
        public const string PrinterNotValid = "当前打印机为「无」或未选择有效输出设备，无法写入布局。请在列表中选择 PDF/物理打印机等。";
        public const string ArgFileInvalid = "当前未选择有效的 .arg 配置文件。";
    }
}
