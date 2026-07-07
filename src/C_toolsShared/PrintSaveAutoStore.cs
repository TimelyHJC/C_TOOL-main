using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace C_toolsShared;

/// <summary>「打印与保存」：V_YYY / V_QQQ 共用打印参数，以及图纸 QSAVE 间隔、默认保存目录。</summary>
public static class PrintSaveAutoStore
{
    private const int FileVersion = 1;
    private const string FileName = "V_YYY_printsave_auto.json";
    private const string PreviousDefaultStyleSheet = "monochrome.ctb";
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    public const string DefaultSaveBasePath = @"D:\C_tool插件";

    public static PrintSaveAutoOptionsDto LoadOrDefault()
    {
        var json = ExceptionHelper.SafeExecute(
            () => File.Exists(FilePath) ? File.ReadAllText(FilePath) : null,
            "读取 V_YYY 打印与保存配置",
            (string?)null);
        if (json == null)
            return DefaultOptions();

        PrintSaveAutoOptionsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<PrintSaveAutoOptionsDto>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析 V_YYY 打印与保存配置", ex);
            return DefaultOptions();
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("解析 V_YYY 打印与保存配置（不支持的 JSON 类型）", ex);
            return DefaultOptions();
        }

        if (dto == null)
            return DefaultOptions();

        if (string.IsNullOrWhiteSpace(dto.SaveBasePath))
            dto.SaveBasePath = DefaultSaveBasePath;
        if (dto.IntervalMinutes < 0)
            dto.IntervalMinutes = 0;
        if (string.IsNullOrWhiteSpace(dto.PrinterName))
            dto.PrinterName = PrintSaveService.DefaultPrinterName;
        if (string.IsNullOrWhiteSpace(dto.CanonicalMediaName))
            dto.CanonicalMediaName = PrintSaveService.MediaAutoMatchText;
        if (string.IsNullOrWhiteSpace(dto.StyleSheet) ||
            string.Equals(dto.StyleSheet.Trim(), PreviousDefaultStyleSheet, StringComparison.OrdinalIgnoreCase))
        {
            dto.StyleSheet = PrintSaveService.DefaultStyleSheet;
        }
        if (string.IsNullOrWhiteSpace(dto.ScaleText))
            dto.ScaleText = PrintSaveService.ScaleFitText;
        if (string.IsNullOrWhiteSpace(dto.PrintOrderRule))
            dto.PrintOrderRule = PrintSaveService.PlotOrderAddedOrder;
        return dto;
    }

    public static void Save(PrintSaveAutoOptionsDto dto)
    {
        dto.Version = FileVersion;
        if (string.IsNullOrWhiteSpace(dto.SaveBasePath))
            dto.SaveBasePath = DefaultSaveBasePath;

        try
        {
            var json = JsonSerializer.Serialize(dto, WriteOptions);
            WriteAllTextAtomic(FilePath, json);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 V_YYY 打印与保存配置（序列化）", ex);
            throw;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 V_YYY 打印与保存配置（路径参数）", ex);
            throw;
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 V_YYY 打印与保存配置（路径过长）", ex);
            throw;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 V_YYY 打印与保存配置（不支持）", ex);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 V_YYY 打印与保存配置（权限）", ex);
            throw;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("写入 V_YYY 打印与保存配置", ex);
            throw;
        }
    }

    private static PrintSaveAutoOptionsDto DefaultOptions() =>
        new()
        {
            Version = FileVersion,
            IntervalMinutes = 0,
            SaveBasePath = DefaultSaveBasePath,
            PrinterName = PrintSaveService.DefaultPrinterName,
            CanonicalMediaName = PrintSaveService.MediaAutoMatchText,
            StyleSheet = PrintSaveService.DefaultStyleSheet,
            CenterPlot = true,
            OffsetX = 0,
            OffsetY = 0,
            FitToPaper = true,
            ScaleText = PrintSaveService.ScaleFitText,
            ScaleLineweights = true,
            AutoMatchOrientation = false,
            Landscape = true,
            UpsideDown = false,
            PrintOrderRule = PrintSaveService.PlotOrderAddedOrder
        };

    private static void WriteAllTextAtomic(string path, string content)
    {
        var directoryPath = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directoryPath))
            Directory.CreateDirectory(directoryPath);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (File.Exists(path))
            File.Replace(tmp, path, destinationBackupFileName: null);
        else
            File.Move(tmp, path);
    }
}
