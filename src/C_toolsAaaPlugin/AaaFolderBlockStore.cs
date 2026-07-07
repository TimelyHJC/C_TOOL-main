using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace C_toolsAaaPlugin;

internal static class AaaFolderBlockStore
{
    private const int FileVersion = 1;
    private const string FileName = "V_AAA_folder_blocks.json";
    private const string DefaultLibraryFolderName = "AA图库";
    private const string RegistryKeyPath = @"Software\C_TOOL";
    private const string RegistryValueInstallRoot = "InstallRoot";

    internal static string FilePath => Path.Combine(C_toolsPaths.UserConfigFolder, FileName);

    internal static AaaFolderBlockSettings Load()
    {
        if (!C_toolsJsonFileStore.TryRead(
                FilePath,
                JsonOptionsCache.ReadRelaxed,
                "Read V_AAA folder settings",
                "Parse V_AAA folder settings",
                C_toolsDiagnostics.LogNonFatal,
                out AaaFolderBlockSettings? settings))
        {
            settings = new AaaFolderBlockSettings();
        }

        settings ??= new AaaFolderBlockSettings();
        settings.FolderPath = NormalizeFolderPath(settings.FolderPath);
        settings.FavoriteFolders = NormalizeFolderList(settings.FavoriteFolders);
        if (settings.FolderPath.Length == 0)
            settings.FolderPath = TryEnsureDefaultLibraryFolder();

        return settings;
    }

    internal static void Save(AaaFolderBlockSettings settings)
    {
        settings.FileVersion = FileVersion;
        settings.FolderPath = NormalizeFolderPath(settings.FolderPath);
        settings.FavoriteFolders = NormalizeFolderList(settings.FavoriteFolders);
        C_toolsJsonFileStore.TryWrite(
            FilePath,
            settings,
            JsonOptionsCache.WriteIndented,
            "Write V_AAA folder settings",
            C_toolsDiagnostics.LogNonFatal,
            serializeOperationName: "Serialize V_AAA folder settings");
    }

    internal static string NormalizeFolderPath(string? path)
    {
        var trimmed = path?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "";

        try
        {
            return Path.GetFullPath(trimmed);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Normalize V_AAA folder path failed (argument)", ex);
            return trimmed;
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Normalize V_AAA folder path failed (not supported)", ex);
            return trimmed;
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Normalize V_AAA folder path failed (path too long)", ex);
            return trimmed;
        }
    }

    internal static List<string> NormalizeFolderList(IEnumerable<string>? folders)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders ?? Array.Empty<string>())
        {
            var normalized = NormalizeFolderPath(folder);
            if (normalized.Length == 0 || !seen.Add(normalized))
                continue;

            result.Add(normalized);
        }

        return result;
    }

    private static string TryEnsureDefaultLibraryFolder()
    {
        var installRoot = TryReadInstallRoot();
        if (installRoot.Length == 0)
            return "";

        try
        {
            var folderPath = Path.GetFullPath(Path.Combine(installRoot, DefaultLibraryFolderName));
            Directory.CreateDirectory(folderPath);
            return folderPath;
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Resolve default V_AAA library folder failed (argument)", ex);
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Resolve default V_AAA library folder failed (not supported)", ex);
        }
        catch (PathTooLongException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Resolve default V_AAA library folder failed (path too long)", ex);
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Create default V_AAA library folder failed", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal("Create default V_AAA library folder failed (unauthorized)", ex);
        }

        return "";
    }

    private static string TryReadInstallRoot()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath, writable: false);
            var value = (key?.GetValue(RegistryValueInstallRoot) as string)?.Trim();
            return string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(value);
        }
        catch (Exception ex)
        {
            C_toolsDiagnostics.LogNonFatal("Read C_TOOL install root failed", ex);
            return "";
        }
    }
}
