using System.IO;
using System.Text;

namespace C_toolsAaaPlugin;

internal static class AaaFolderBlockCatalog
{
    internal static IReadOnlyList<AaaBlockCatalogItem> Load(string folderPath, bool includeSubfolders)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            return Array.Empty<AaaBlockCatalogItem>();

        var root = Path.GetFullPath(folderPath);
        var singleBlocks = new List<AaaBlockCatalogItem>();
        var comboCandidates = new List<ComboCandidate>();
        CollectItems(root, root, includeSubfolders, singleBlocks, comboCandidates);

        var comboItems = new List<AaaBlockCatalogItem>();
        foreach (var comboCandidate in comboCandidates)
        {
            var comboItem = TryCreateComboItem(root, comboCandidate, singleBlocks);
            if (comboItem != null)
                comboItems.Add(comboItem);
        }

        return singleBlocks
            .Concat(comboItems)
            .OrderBy(x => x.RelativeFolderDisplay, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Kind)
            .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void CollectItems(
        string rootFolder,
        string currentFolder,
        bool includeSubfolders,
        ICollection<AaaBlockCatalogItem> singleBlocks,
        ICollection<ComboCandidate> comboCandidates)
    {
        foreach (var directory in Directory.EnumerateDirectories(currentFolder))
        {
            if (AaaBlockComboPackageStore.IsComboPackageDirectory(directory))
            {
                comboCandidates.Add(new ComboCandidate
                {
                    ComboPath = directory,
                    RelativeFolder = GetRelativeFolder(rootFolder, Directory.GetParent(directory)?.FullName ?? rootFolder)
                });
                continue;
            }

            if (includeSubfolders)
                CollectItems(rootFolder, directory, true, singleBlocks, comboCandidates);
        }

        foreach (var path in Directory.EnumerateFiles(currentFolder, "*.dwg", SearchOption.TopDirectoryOnly))
        {
            var file = new FileInfo(path);
            var relativeFolder = GetRelativeFolder(rootFolder, file.DirectoryName ?? rootFolder);
            singleBlocks.Add(new AaaBlockCatalogItem
            {
                Kind = AaaBlockCatalogItemKind.Dwg,
                LibraryRootPath = rootFolder,
                DisplayName = Path.GetFileNameWithoutExtension(file.Name),
                RelativeFolder = relativeFolder,
                FullPath = file.FullName,
                PreviewSourcePath = file.FullName,
                PreviewLastWriteTimeUtc = file.LastWriteTimeUtc,
                FileSizeText = FormatFileSize(file.Length),
                ModifiedText = file.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                LastWriteTimeUtc = file.LastWriteTimeUtc,
                CategoryTags = BuildCategoryTags(relativeFolder)
            });
        }

        foreach (var path in Directory.EnumerateFiles(currentFolder, "*", SearchOption.TopDirectoryOnly))
        {
            if (!AaaBlockComboPackageStore.IsComboDefinitionFile(path))
                continue;

            comboCandidates.Add(new ComboCandidate
            {
                ComboPath = path,
                RelativeFolder = GetRelativeFolder(rootFolder, currentFolder)
            });
        }
    }

    private static AaaBlockCatalogItem? TryCreateComboItem(
        string rootFolder,
        ComboCandidate candidate,
        IReadOnlyList<AaaBlockCatalogItem> singleBlocks)
    {
        var manifest = AaaBlockComboPackageStore.Load(candidate.ComboPath);
        if (manifest == null)
            return null;

        var manifestPath = AaaBlockComboPackageStore.GetManifestPath(candidate.ComboPath);
        var manifestInfo = new FileInfo(manifestPath);
        var comboInfo = new FileInfo(candidate.ComboPath);
        var previewSourcePath =
            AaaBlockComboPackageStore.ResolvePreviewSourcePath(candidate.ComboPath, manifest) ??
            ResolvePreviewSourcePath(rootFolder, manifest, singleBlocks) ??
            "";

        var previewLastWriteTimeUtc = manifestInfo.Exists ? manifestInfo.LastWriteTimeUtc : DateTime.UtcNow;
        if (previewSourcePath.Length > 0 && File.Exists(previewSourcePath))
            previewLastWriteTimeUtc = File.GetLastWriteTimeUtc(previewSourcePath);

        var modifiedTime = manifestInfo.Exists
            ? manifestInfo.LastWriteTime
            : comboInfo.Exists
                ? comboInfo.LastWriteTime
                : Directory.Exists(candidate.ComboPath)
                    ? Directory.GetLastWriteTime(candidate.ComboPath)
                    : DateTime.MinValue;
        var lastWriteTimeUtc = manifestInfo.Exists
            ? manifestInfo.LastWriteTimeUtc
            : comboInfo.Exists
                ? comboInfo.LastWriteTimeUtc
                : Directory.Exists(candidate.ComboPath)
                    ? Directory.GetLastWriteTimeUtc(candidate.ComboPath)
                    : DateTime.MinValue;

        return new AaaBlockCatalogItem
        {
            Kind = AaaBlockCatalogItemKind.Combo,
            LibraryRootPath = rootFolder,
            DisplayName = manifest.DisplayName.Length == 0
                ? AaaBlockComboPackageStore.GetDisplayName(candidate.ComboPath)
                : manifest.DisplayName,
            RelativeFolder = candidate.RelativeFolder,
            FullPath = candidate.ComboPath,
            ManifestPath = manifestPath,
            PreviewSourcePath = previewSourcePath,
            PreviewLastWriteTimeUtc = previewLastWriteTimeUtc,
            FileSizeText = FormatFileSize(GetComboArtifactSize(candidate.ComboPath, manifestPath)),
            ModifiedText = modifiedTime == DateTime.MinValue
                ? ""
                : modifiedTime.ToString("yyyy-MM-dd HH:mm"),
            LastWriteTimeUtc = lastWriteTimeUtc,
            MemberCount = manifest.Members.Count,
            CategoryTags = BuildCategoryTags(candidate.RelativeFolder)
        };
    }

    private static string? ResolvePreviewSourcePath(
        string rootFolder,
        AaaBlockComboManifest manifest,
        IReadOnlyList<AaaBlockCatalogItem> singleBlocks)
    {
        foreach (var member in manifest.Members)
        {
            var resolved = AaaComboLibraryReferenceResolver.ResolveLibraryBlock(member, singleBlocks, rootFolder);
            if (resolved != null && File.Exists(resolved.FullPath))
                return resolved.FullPath;
        }

        return null;
    }

    private static long GetComboArtifactSize(string comboPath, string manifestPath)
    {
        if (File.Exists(comboPath))
            return TryGetFileSize(comboPath);

        if (Directory.Exists(comboPath))
            return GetDirectorySize(comboPath);

        return File.Exists(manifestPath)
            ? TryGetFileSize(manifestPath)
            : 0;
    }

    private static string GetRelativeFolder(string rootFolder, string currentFolder)
    {
        try
        {
            var rootUri = new Uri(AppendDirectorySeparator(rootFolder));
            var currentUri = new Uri(AppendDirectorySeparator(currentFolder));
            var relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(currentUri).ToString())
                .Replace('/', Path.DirectorySeparatorChar)
                .Trim();
            if (relative == "." || relative == string.Empty)
                return "";
            return relative.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (UriFormatException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 计算相对目录失败", ex);
            return GetRelativeFolderFallback(rootFolder, currentFolder);
        }
        catch (InvalidOperationException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 计算相对目录失败（无效操作）", ex);
            return GetRelativeFolderFallback(rootFolder, currentFolder);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal("V_AAA 计算相对目录失败（参数）", ex);
            return GetRelativeFolderFallback(rootFolder, currentFolder);
        }
    }

    private static string AppendDirectorySeparator(string path)
    {
        if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            return path;
        return path + Path.DirectorySeparatorChar;
    }

    private static IReadOnlyList<string> BuildCategoryTags(string relativeFolder)
    {
        if (string.IsNullOrWhiteSpace(relativeFolder))
            return Array.Empty<string>();

        var segments = relativeFolder
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToArray();
        if (segments.Length == 0)
            return Array.Empty<string>();

        var tags = new List<string>(segments.Length);
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            if (builder.Length > 0)
                builder.Append('/');
            builder.Append(segment);
            tags.Add(builder.ToString());
        }

        return tags;
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024d:0.#} KB";
        return $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private static long GetDirectorySize(string directoryPath)
    {
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            total += TryGetFileSize(file);

        return total;
    }

    private static string GetRelativeFolderFallback(string rootFolder, string currentFolder)
    {
        return string.Equals(rootFolder, currentFolder, StringComparison.OrdinalIgnoreCase)
            ? ""
            : currentFolder;
    }

    private static long TryGetFileSize(string filePath)
    {
        try
        {
            return new FileInfo(filePath).Length;
        }
        catch (IOException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 读取图库文件大小失败：{filePath}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 读取图库文件大小失败（权限）：{filePath}", ex);
        }
        catch (ArgumentException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 读取图库文件大小失败（参数）：{filePath}", ex);
        }
        catch (NotSupportedException ex)
        {
            C_toolsDiagnostics.LogNonFatal($"V_AAA 读取图库文件大小失败（格式）：{filePath}", ex);
        }

        return 0;
    }

    private sealed class ComboCandidate
    {
        internal string ComboPath { get; set; } = "";
        internal string RelativeFolder { get; set; } = "";
    }
}
