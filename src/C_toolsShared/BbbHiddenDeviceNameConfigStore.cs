using System.IO;

namespace C_toolsShared;

/// <summary>User-configurable Excel path for the V_BBB device list workflow.</summary>
public static class BbbHiddenDeviceNameConfigStore
{
    private const string FileName = "V_BBB_hidden_device_name_workbook.txt";

    public const string DefaultWorkbookPath = @"C:\Users\V2000\Desktop\设备清单.xlsx";
    public const string DefaultWorksheetName = "设备名称查找";
    public const string DefaultColumnHeader = "规范物料名称";
    public const string DefaultBaseTag = "设备名称";

    public static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    public static string LoadWorkbookPath()
    {
        var text = C_toolsTextFileStore.TryReadAllText(FilePath, "读取 V_BBB 设备清单路径");
        return text == null ? DefaultWorkbookPath : NormalizeWorkbookPath(text);
    }

    public static void SaveWorkbookPath(string? workbookPath)
    {
        _ = C_toolsTextFileStore.TryWriteAllText(
            FilePath,
            NormalizeWorkbookPath(workbookPath),
            "写入 V_BBB 设备清单路径");
    }

    public static string NormalizeWorkbookPath(string? workbookPath)
    {
        var trimmed = workbookPath?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? DefaultWorkbookPath : trimmed!;
    }
}
