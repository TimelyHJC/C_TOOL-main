namespace C_toolsDddPlugin;

/// <summary>文字标注浮窗（DDD）界面文案，供 XAML x:Static 与代码共用。</summary>
public static class DddPanelUiStrings
{
    public const string ToolTipTitleClose = "关闭";
    public const string ToolTipTxtAdd = "将输入加入当前列表，支持多行文字";
    public const string ToolTipInsertFromInputButton = "将输入加入当前列表，并按当前模式插入到 CAD";
    public const string ToolTipPickTextButton = "隐藏窗口后到 CAD 中选择单行文字或多行文字，并回填到输入框";
    public const string ToolTipMLeaderStyleCombo = "当前图形使用的多重引线样式（CMLEADERSTYLE）";
    public const string ToolTipSetLeaderButton = "打开多重引线样式管理器";
    public const string ToolTipInsertWithLeaderToggle = "开启后列表点击插入带引线标注；关闭后改为纯文字单点插入";
    public const string ToolTipClearList = "清空当前标签页列表";
    public const string ToolTipImportTable = "从 Excel（.xlsx）导入到当前列表";
    public const string ToolTipSelectionMode = "开启后列表点击仅用于选择行，便于批量删除";
    public const string ToolTipDeleteSelectedRows = "删除当前标签页里选中的行";

    public const string StatusCannotSwitchStyle = "无法切换多重引线样式。";
    public const string StatusStyleSetSuccessFormat = "已设置当前样式为：{0}";
    public const string StatusCannotSwitchStyleWithReasonFormat = "无法切换多重引线样式：{0}";
    public const string StatusCannotOpenLeaderSettingsFormat = "无法打开多重引线样式：{0}";

    public const string DialogTitleImportExcel = "选择要导入的 Excel 工作簿";

    public const string MsgImportFailedFallback = "未能读取 Excel 文件。";
    public const string MsgImportNoRowsFallback = "文件中没有可导入的数据行。";

    public const string StatusExcelImportNotRun = "Excel 导入未执行。";
    public const string StatusExcelImportNoRowsAdded = "Excel 导入未增加行。";
    public const string StatusExcelImportResultFormat = "已从 Excel 追加 {0} 条（文件内有效行 {1}，因与列表重复跳过 {2} 条）。";
    public const string StatusExcelImportFailedFormat = "Excel 导入失败：{0}";

    public const string TabNameTextList = "文字列表";
    public const string TabNamePropList = "道具列表";
    public const string TabNameMaterialList = "材料列表";
    public const string TabNameGeneric = "当前列表";

    public const string StatusTabAlreadyEmptyFormat = "「{0}」已是空的。";
    public const string ClearListConfirmFormat = "确定清空「{0}」中的 {1} 条记录吗？";
    public const string StatusSelectTabFirst = "请先选择文字 / 道具 / 材料标签页。";
    public const string StatusListClearedFormat = "已清空「{0}」。";

    public const string StatusAddNeedText = "请输入要添加的内容。";
    public const string StatusInsertNeedText = "请输入要插入的内容。";
    public const string StatusAddedRemarkFormat = "已添加文字，当前共 {0} 条。";
    public const string StatusAddedPropFormat = "已添加道具，当前共 {0} 条。";
    public const string StatusAddedMaterialFormat = "已添加材料，当前共 {0} 条。";
    public const string StatusPickingTextFromCad = "请在 CAD 中选择文字，完成后会自动回填到输入框。";
    public const string StatusPickTextCancelled = "未选择任何文字。";
    public const string StatusPickTextNoDocument = "当前没有活动图纸。";
    public const string StatusPickTextFailedFormat = "选择文字失败：{0}";
    public const string StatusPickedTextToInputFormat = "已读取 {0} 条文字到输入框。";

    public const string StatusLeaderMainColumnEmpty = "该行主列为空，无法插入文字。";
    public const string StatusInsertLeaderErrorFormat = "无法插入引线：{0}";
    public const string StatusInsertTextErrorFormat = "无法插入文字：{0}";
    public const string StatusInsertModeLeader = "已切换为带引线插入。";
    public const string StatusInsertModePlainText = "已切换为纯文字插入。";
    public const string StatusSelectionModeEnabled = "已进入选择模式，可多选后删除。";

    public const string StatusRemarkTextEmpty = "该行文字为空。";
    public const string StatusPropNameEmpty = "道具名称为空。";
    public const string StatusMaterialNameEmpty = "材料名称为空。";
    public const string StatusAppliedTextToDrawing = "已将文字写入选中的图中文字。";
    public const string StatusDeleteNeedSelectionFormat = "请先在「{0}」中选中要删除的行。";
    public const string StatusDeletedSelectedRowsFormat = "已从「{0}」删除 {1} 条选中记录。";
}
