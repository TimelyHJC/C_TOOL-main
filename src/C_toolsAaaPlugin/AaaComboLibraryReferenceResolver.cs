using System.IO;

namespace C_toolsAaaPlugin;

internal static class AaaComboLibraryReferenceResolver
{
    internal static IReadOnlyList<AaaBlockCatalogItem> LoadSingleBlocks(string libraryRootPath)
    {
        return AaaFolderBlockCatalog.Load(libraryRootPath, true)
            .Where(x => !x.IsCombo)
            .ToList();
    }

    internal static AaaBlockCatalogItem? ResolveLibraryBlock(
        AaaBlockComboMember member,
        IReadOnlyList<AaaBlockCatalogItem> libraryItems,
        string libraryRootPath)
    {
        if (member == null || libraryItems.Count == 0 || string.IsNullOrWhiteSpace(libraryRootPath))
            return null;

        var deviceName = GetDeviceName(member);
        if (deviceName.Length == 0)
            return null;

        var candidates = libraryItems
            .Where(x =>
                !x.IsCombo &&
                string.Equals(
                    AaaBlockLibraryNameHelper.GetDeviceName(x.DisplayName),
                    deviceName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            return null;

        var preferredRelativePath = NormalizeRelativePath(member.SourceRelativePath);
        if (preferredRelativePath.Length > 0)
        {
            var expectedPath = BuildSafePath(libraryRootPath, preferredRelativePath);
            var exactPathMatch = candidates.FirstOrDefault(x =>
                string.Equals(
                    Path.GetFullPath(x.FullPath),
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase));
            if (exactPathMatch != null)
                return exactPathMatch;
        }

        var displayName = (member.DisplayName ?? "").Trim();
        if (displayName.Length > 0)
        {
            var exactNameMatch = candidates
                .Where(x => string.Equals(x.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .FirstOrDefault();
            if (exactNameMatch != null)
                return exactNameMatch;
        }

        var sourceRelativeFolder = NormalizeRelativeFolder(member.SourceRelativeFolder);
        if (sourceRelativeFolder.Length > 0)
        {
            var sameFolderMatch = candidates
                .Where(x =>
                    string.Equals(
                        NormalizeRelativeFolder(x.RelativeFolder),
                        sourceRelativeFolder,
                        StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.LastWriteTimeUtc)
                .FirstOrDefault();
            if (sameFolderMatch != null)
                return sameFolderMatch;
        }

        return candidates
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    internal static string BuildRelativePath(string rootFolder, string fullPath)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || string.IsNullOrWhiteSpace(fullPath))
            return "";

        try
        {
            var rootUri = new Uri(AppendDirectorySeparator(Path.GetFullPath(rootFolder)));
            var fullUri = new Uri(Path.GetFullPath(fullPath));
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(fullUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar)
                .Trim();
        }
        catch (UriFormatException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 计算组合引用相对路径失败", ex);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 计算组合引用相对路径失败（无效操作）", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 计算组合引用相对路径失败（参数）", ex);
        }

        return Path.GetFileName(fullPath) ?? "";
    }

    private static string GetDeviceName(AaaBlockComboMember member)
    {
        var deviceName = (member.DeviceName ?? "").Trim();
        if (deviceName.Length > 0)
            return deviceName;

        return AaaBlockLibraryNameHelper.GetDeviceName(member.DisplayName);
    }

    private static string NormalizeRelativePath(string? relativePath)
    {
        var value = (relativePath ?? "").Trim();
        if (Path.IsPathRooted(value))
            return "";

        return value
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static string NormalizeRelativeFolder(string? relativeFolder)
    {
        return NormalizeRelativePath(relativeFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string BuildSafePath(string rootFolder, string relativePath)
    {
        var root = AppendDirectorySeparator(Path.GetFullPath(rootFolder));
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        return resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? resolved
            : rootFolder;
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            return path;

        return path + Path.DirectorySeparatorChar;
    }
}
